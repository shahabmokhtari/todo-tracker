using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TodoTracker.Server.Plugins.AgentChat;

/// <summary>How to start an agent: the command, its arguments, the working folder, and extra environment variables.</summary>
public sealed record AgentLaunch(string Id, string Command, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string?> Environment);

/// <summary>The agent stopped, returned an error, or broke the protocol.</summary>
public sealed class AcpException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// JSON-RPC 2.0 over an agent process's stdio (newline-delimited JSON), as the Agent Client Protocol specifies.
/// stdout and stderr are always drained (a full pipe would freeze the agent); stderr keeps only its last 64 KB for
/// diagnostics. When the process exits every pending request fails. Disposing kills the agent and its child processes.
/// </summary>
public sealed class AcpConnection : IAsyncDisposable
{
    private const int StderrLimit = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Process _process;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly StringBuilder _stderr = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private long _nextId;
    private int _disposed;

    private AcpConnection(Process process)
    {
        _process = process;
    }

    /// <summary>Raised for notifications (e.g. <c>session/update</c>), on a background thread.</summary>
    public event Action<string, JsonElement>? Notification;

    /// <summary>Answers requests from the agent (e.g. <c>session/request_permission</c>). Unset: they get an error.</summary>
    public Func<string, JsonElement, CancellationToken, Task<object?>>? RequestHandler { get; set; }

    /// <summary>Completes when the agent process has exited.</summary>
    public Task Exited => _exited.Task;

    public int ProcessId => _process.Id;

    public string StderrTail
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString();
            }
        }
    }

    public static AcpConnection Start(AgentLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var start = new ProcessStartInfo(launch.Command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = launch.WorkingDirectory,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in launch.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in launch.Environment)
        {
            start.Environment[name] = value;
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new AcpException($"Couldn't start {launch.Command}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new AcpException($"Couldn't start {launch.Command}: {ex.Message}", ex);
        }

        var connection = new AcpConnection(process);
        _ = connection.ReadStdoutAsync();
        _ = connection.ReadStderrAsync();
        _ = connection.WatchExitAsync();
        return connection;
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_exited.Task.IsCompleted)
        {
            throw Stopped();
        }

        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = ToNode(parameters) }).ConfigureAwait(false);
            return await answer.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, object? parameters) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = ToNode(parameters) });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
        }

        // Stop the agent and everything it started (MCP servers, npx's node) while it is still alive: once it exits,
        // its children are orphans that a tree kill can no longer find.
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        await _exited.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _process.Dispose();
        _write.Dispose();
        _stop.Dispose();
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        _ => JsonSerializer.SerializeToNode(value, value.GetType(), Json),
    };

    private AcpException Stopped()
    {
        var tail = StderrTail.Trim();
        var last = tail.Length > 400 ? "…" + tail[^400..] : tail;
        return new AcpException(string.IsNullOrEmpty(last) ? "The agent stopped." : $"The agent stopped: {last}");
    }

    private async Task WriteAsync(JsonObject message)
    {
        if (_exited.Task.IsCompleted)
        {
            throw Stopped();
        }

        await _write.WaitAsync().ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteAsync(message.ToJsonString() + "\n").ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new AcpException("The agent stopped listening.", ex);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task ReadStdoutAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length > 0)
                {
                    Dispatch(line);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private async Task ReadStderrAsync()
    {
        var buffer = new char[4096];
        try
        {
            while (await _process.StandardError.ReadAsync(buffer, _stop.Token).ConfigureAwait(false) is var read and > 0)
            {
                lock (_stderr)
                {
                    _stderr.Append(buffer, 0, read);
                    if (_stderr.Length > StderrLimit)
                    {
                        _stderr.Remove(0, _stderr.Length - StderrLimit);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private async Task WatchExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }

        _exited.TrySetResult();
        var error = Stopped();
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(error);
        }
    }

    private void Dispatch(string line)
    {
        JsonElement message;
        try
        {
            using var doc = JsonDocument.Parse(line);
            message = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return; // Not protocol traffic (some agents print banners); ignore it.
        }

        if (message.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var hasMethod = message.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String;
        var hasId = message.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String;
        var parameters = message.TryGetProperty("params", out var p) ? p : default;
        if (!hasMethod && hasId)
        {
            if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number) && _pending.TryGetValue(number, out var waiting))
            {
                if (message.TryGetProperty("error", out var error))
                {
                    var text = error.TryGetProperty("message", out var m) ? m.GetString() : error.GetRawText();
                    waiting.TrySetException(new AcpException(text ?? "The agent returned an error."));
                }
                else
                {
                    waiting.TrySetResult(message.TryGetProperty("result", out var result) ? result : default);
                }
            }

            return;
        }

        if (!hasMethod)
        {
            return;
        }

        var method = methodElement.GetString()!;
        if (!hasId)
        {
            Notification?.Invoke(method, parameters);
            return;
        }

        _ = AnswerAsync(method, id, parameters);
    }

    private async Task AnswerAsync(string method, JsonElement id, JsonElement parameters)
    {
        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()) };
        try
        {
            if (RequestHandler is not { } handler)
            {
                reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"{method} isn't supported." };
            }
            else
            {
                reply["result"] = ToNode(await handler(method, parameters, _stop.Token).ConfigureAwait(false)) ?? new JsonObject();
            }
        }
        catch (NotSupportedException)
        {
            reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"{method} isn't supported." };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationCanceledException or JsonException)
        {
            reply["error"] = new JsonObject { ["code"] = -32603, ["message"] = ex.Message };
        }

        try
        {
            await WriteAsync(reply).ConfigureAwait(false);
        }
        catch (AcpException)
        {
            // The agent is gone; nothing to answer.
        }
    }
}
