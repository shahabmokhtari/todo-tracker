using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TodoTracker.Server.Plugins.AgentChat;

/// <summary>How to start an agent: the command, its arguments, the working folder, and extra environment variables.</summary>
public sealed record AgentLaunch(string Id, string Command, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string?> Environment)
{
    /// <summary>Another way to start the same agent if this one can't sign in (e.g. with the person's own settings).</summary>
    public AgentLaunch? SignInFallback { get; init; }
}

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

    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly IAgentProcess _process;
    private readonly StreamWriter _input;
    private readonly StreamReader _output;
    private readonly StreamReader _diagnostics;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly StringBuilder _stderr = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private long _nextId;
    private int _disposed;

    private AcpConnection(IAgentProcess process)
    {
        _process = process;
        _input = new StreamWriter(process.Input, Utf8) { AutoFlush = false };
        _output = new StreamReader(process.Output, Utf8);
        _diagnostics = new StreamReader(process.Diagnostics, Utf8);
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
        IAgentProcess process;
        try
        {
            process = AgentProcess.Start(launch);
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
            _input.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }

        // Stop the agent and everything it started (MCP servers, npx's node).
        _process.KillTree();
        try
        {
            await _exited.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

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

    /// <summary>How the agent ended (null while it runs).</summary>
    public int? ExitCode { get; private set; }

    private AcpException Stopped()
    {
        var tail = StderrTail.Trim();
        var last = tail.Length > 400 ? "…" + tail[^400..] : tail;
        var code = ExitCode switch
        {
            null => string.Empty,
            var c when c < 0 => $" (exit code 0x{unchecked((uint)c):X8})",
            var c => $" (exit code {c})",
        };
        return new AcpException(string.IsNullOrEmpty(last) ? $"The agent stopped{code}." : $"The agent stopped{code}: {last}");
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
            await _input.WriteAsync(message.ToJsonString() + "\n").ConfigureAwait(false);
            await _input.FlushAsync().ConfigureAwait(false);
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
            while (await _output.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
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
            while (await _diagnostics.ReadAsync(buffer, _stop.Token).ConfigureAwait(false) is var read and > 0)
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
        ExitCode = await _process.Exited.ConfigureAwait(false);
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
