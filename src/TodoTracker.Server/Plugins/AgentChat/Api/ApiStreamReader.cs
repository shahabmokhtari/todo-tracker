using System.Text;
using System.Text.Json;

namespace TodoTracker.Server.Plugins.AgentChat;

/// <summary>Which wire format a model speaks.</summary>
public enum ApiKind
{
    /// <summary>OpenAI Chat Completions (also Azure OpenAI, OpenRouter, Ollama, LM Studio, …).</summary>
    OpenAI,

    /// <summary>Anthropic Messages.</summary>
    Anthropic,
}

/// <summary>A tool the model asked to run; <see cref="Arguments"/> is a JSON object.</summary>
public sealed record ApiToolCall(string Id, string Name, string Arguments);

/// <summary>What one streamed answer held besides its text.</summary>
public sealed record ApiTurn(string Text, IReadOnlyList<ApiToolCall> ToolCalls, string? StopReason);

/// <summary>The model's API answered with an error.</summary>
public sealed class ApiChatException(string message, int? status = null) : Exception(message)
{
    public int? Status { get; } = status;
}

/// <summary>
/// Reads a streamed answer (server-sent events) from an OpenAI- or Anthropic-style API: text as it comes, tool calls
/// once their pieces are complete. Keep-alives, comments and unknown events are skipped.
/// </summary>
public static class ApiStreamReader
{
    public static async Task<ApiTurn> ReadAsync(ApiKind kind, Stream body, Action<string> onText, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(onText);
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
        string? stop = null;
        using var reader = new StreamReader(body, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]")
            {
                continue;
            }

            JsonElement json;
            try
            {
                using var doc = JsonDocument.Parse(data);
                json = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            if (json.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (json.TryGetProperty("error", out var error))
            {
                throw new ApiChatException(Str(error, "message") ?? error.ToString());
            }

            var piece = kind == ApiKind.OpenAI ? OpenAI(json, calls, ref stop) : Anthropic(json, calls, ref stop);
            if (piece is { Length: > 0 })
            {
                text.Append(piece);
                onText(piece);
            }
        }

        var toolCalls = calls.Values
            .Where(c => c.Name.Length > 0)
            .Select((c, i) => new ApiToolCall(c.Id.Length > 0 ? c.Id : $"call_{i}", c.Name, c.Args.Length > 0 ? c.Args.ToString() : "{}"))
            .ToList();
        return new ApiTurn(text.ToString(), toolCalls, stop);
    }

    private static string? OpenAI(JsonElement json, SortedDictionary<int, (string Id, string Name, StringBuilder Args)> calls, ref string? stop)
    {
        if (!json.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var choice = choices[0];
        if (Str(choice, "finish_reason") is { } finish)
        {
            stop = finish;
        }

        if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var index = call.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : calls.Count;
                var current = calls.TryGetValue(index, out var c) ? c : (string.Empty, string.Empty, new StringBuilder());
                var function = call.TryGetProperty("function", out var f) ? f : default;
                calls[index] = (
                    Str(call, "id") ?? current.Item1,
                    current.Item2 + (Str(function, "name") ?? string.Empty),
                    current.Item3.Append(Str(function, "arguments") ?? string.Empty));
            }
        }

        return Str(delta, "content");
    }

    private static string? Anthropic(JsonElement json, SortedDictionary<int, (string Id, string Name, StringBuilder Args)> calls, ref string? stop)
    {
        var index = json.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : 0;
        switch (Str(json, "type"))
        {
            case "content_block_start":
                if (json.TryGetProperty("content_block", out var block) && Str(block, "type") == "tool_use")
                {
                    calls[index] = (Str(block, "id") ?? string.Empty, Str(block, "name") ?? string.Empty, new StringBuilder());
                }

                return json.TryGetProperty("content_block", out var start) ? Str(start, "text") : null;
            case "content_block_delta":
                var delta = json.TryGetProperty("delta", out var d) ? d : default;
                switch (Str(delta, "type"))
                {
                    case "text_delta":
                        return Str(delta, "text");
                    case "input_json_delta" when calls.TryGetValue(index, out var call):
                        call.Args.Append(Str(delta, "partial_json"));
                        return null;
                    default:
                        return null;
                }

            case "message_delta":
                if (json.TryGetProperty("delta", out var message) && Str(message, "stop_reason") is { } reason)
                {
                    stop = reason;
                }

                return null;
            default:
                return null;
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
