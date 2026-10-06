using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace TodoTracker.Server.Plugins.AgentChat;

/// <summary>What a tool answered: its text, and whether it's an error (the model is told either way).</summary>
public sealed record ChatToolResult(string Text, bool IsError);

/// <summary>The tools an API model may call in a chat.</summary>
public interface IChatTools : IAsyncDisposable
{
    Task<IReadOnlyList<ApiToolSpec>> ListAsync(CancellationToken cancellationToken);

    Task<ChatToolResult> CallAsync(string name, string arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Todo Tracker's own MCP tools, reached over the app's <c>/mcp</c> endpoint: the same tools Copilot and Claude get, and
/// whatever the model changes is credited to it (the MCP client is named after the model).
/// </summary>
public sealed class McpChatTools(Func<HttpClient> http, string modelName) : IChatTools
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;
    private IReadOnlyList<ApiToolSpec>? _tools;

    public async Task<IReadOnlyList<ApiToolSpec>> ListAsync(CancellationToken cancellationToken)
    {
        if (_tools is { } known)
        {
            return known;
        }

        McpClient client;
        IList<McpClientTool> tools;
        try
        {
            client = await ClientAsync(cancellationToken).ConfigureAwait(false);
            tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Connect again once (the session may have been let go of).
            await ResetAsync().ConfigureAwait(false);
            try
            {
                client = await ClientAsync(cancellationToken).ConfigureAwait(false);
                tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException($"Couldn't reach Todo Tracker's own tools: {ex.Message}", ex);
            }
        }

        _tools = [.. tools.Select(t => new ApiToolSpec(
            t.Name,
            t.Description ?? string.Empty,
            System.Text.Json.Nodes.JsonNode.Parse(t.ProtocolTool.InputSchema.GetRawText())!,
            t.ProtocolTool.Annotations?.ReadOnlyHint == true))];
        return _tools;
    }

    public async Task<ChatToolResult> CallAsync(string name, string arguments, CancellationToken cancellationToken)
    {
        Dictionary<string, object?> args;
        try
        {
            args = string.IsNullOrWhiteSpace(arguments)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments)?.ToDictionary(p => p.Key, p => (object?)p.Value) ?? [];
        }
        catch (JsonException)
        {
            return new ChatToolResult("The arguments weren't valid JSON; call the tool again with a JSON object.", true);
        }

        try
        {
            return await CallOnceAsync(name, args, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // The app let go of this MCP session (idle for long): connect again. The call never ran, so it's safe.
            await ResetAsync().ConfigureAwait(false);
            try
            {
                return await CallOnceAsync(name, args, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException again)
            {
                return new ChatToolResult($"Todo Tracker's tools couldn't be reached: {again.Message}", true);
            }
        }
        catch (HttpRequestException ex)
        {
            return new ChatToolResult($"Todo Tracker's tools couldn't be reached: {ex.Message}", true);
        }
    }

    private async Task<ChatToolResult> CallOnceAsync(string name, Dictionary<string, object?> args, CancellationToken cancellationToken)
    {
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await client.CallToolAsync(name, args, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ChatToolResult(string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text)), result.IsError ?? false);
        }
        catch (ModelContextProtocol.McpException ex)
        {
            return new ChatToolResult(ex.Message, true);
        }
    }

    private async Task ResetAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client is { } stale)
            {
                _client = null;
                await stale.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is { } client)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    private async Task<McpClient> ClientAsync(CancellationToken cancellationToken)
    {
        if (_client is { } ready)
        {
            return ready;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is null)
            {
                var httpClient = http();
                var transport = new HttpClientTransport(
                    new HttpClientTransportOptions { Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
                    httpClient,
                    ownsHttpClient: true);
                _client = await McpClient.CreateAsync(transport, new McpClientOptions { ClientInfo = new Implementation { Name = modelName, Version = McpInfo.Version } }, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }
}
