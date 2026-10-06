using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TodoTracker.Server.Plugins.AgentChat;

/// <summary>A tool the model may call: Todo Tracker's MCP tools, with their JSON schema, and whether it only reads.</summary>
public sealed record ApiToolSpec(string Name, string Description, JsonNode InputSchema, bool ReadOnly = false);

/// <summary>
/// One message of an API conversation, in a form that fits both OpenAI and Anthropic: user and assistant text, the
/// tools the assistant called, and each tool's result.
/// </summary>
public sealed record ApiMessage(string Role, string? Text = null, IReadOnlyList<ApiToolCall>? ToolCalls = null, string? ToolCallId = null, string? ToolName = null);

/// <summary>
/// A model reached with an API key. <paramref name="Preset"/> is how its address works: openai, azure, openrouter,
/// ollama, lmstudio, anthropic, or openai-compatible / anthropic-compatible for anything else.
/// </summary>
public sealed record ApiModel(string Id, string Preset, string Name, string BaseUrl, string Model)
{
    public const string AzureApiVersion = "2024-10-21";

    private static readonly Dictionary<string, ApiKind> Presets = new(StringComparer.Ordinal)
    {
        ["openai"] = ApiKind.OpenAI,
        ["azure"] = ApiKind.OpenAI,
        ["openrouter"] = ApiKind.OpenAI,
        ["ollama"] = ApiKind.OpenAI,
        ["lmstudio"] = ApiKind.OpenAI,
        ["openai-compatible"] = ApiKind.OpenAI,
        ["anthropic"] = ApiKind.Anthropic,
        ["anthropic-compatible"] = ApiKind.Anthropic,
    };

    public ApiKind Kind => Presets[Preset];

    /// <summary>Whether it runs on this computer (task details then never leave it).</summary>
    public bool IsLocal => new Uri(BaseUrl).IsLoopback;

    /// <summary>Checks and tidies what was typed in; task details go to this address, so only https leaves the computer.</summary>
    public static ApiModel Create(string preset, string name, string baseUrl, string model, string? id = null)
    {
        if (!Presets.ContainsKey(preset ?? string.Empty))
        {
            throw new ArgumentException($"Unknown kind of model \"{preset}\".", nameof(preset));
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Which model (or Azure deployment)?", nameof(model));
        }

        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new ArgumentException("Use an https address (plain http only for a model on this computer, like Ollama or LM Studio).", nameof(baseUrl));
        }

        var clean = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new ApiModel(id ?? Guid.NewGuid().ToString("N")[..12], preset!, string.IsNullOrWhiteSpace(name) ? model.Trim() : name.Trim(), clean, model.Trim());
    }

    internal Uri Endpoint => Preset switch
    {
        "azure" => new Uri($"{BaseUrl}/openai/deployments/{Uri.EscapeDataString(Model)}/chat/completions?api-version={AzureApiVersion}"),
        _ when Kind == ApiKind.Anthropic => new Uri(BaseUrl.EndsWith("/v1", StringComparison.Ordinal) ? $"{BaseUrl}/messages" : $"{BaseUrl}/v1/messages"),
        _ => new Uri($"{BaseUrl}/chat/completions"),
    };
}

/// <summary>Sends a conversation to a model and streams its answer (text, and the tools it wants to call).</summary>
public sealed class ApiChatClient(HttpClient http)
{
    private const int MaxOutputTokens = 4096;

    public async Task<ApiTurn> CompleteAsync(ApiModel model, string key, string system, IReadOnlyList<ApiMessage> messages, IReadOnlyList<ApiToolSpec> tools, Action<string> onText, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        using var request = new HttpRequestMessage(HttpMethod.Post, model.Endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        JsonObject body;
        if (model.Kind == ApiKind.Anthropic)
        {
            if (key.Length > 0)
            {
                request.Headers.Add("x-api-key", key);
            }

            request.Headers.Add("anthropic-version", "2023-06-01");
            body = AnthropicBody(model, system, messages, tools);
        }
        else
        {
            if (key.Length > 0)
            {
                if (model.Preset == "azure")
                {
                    request.Headers.Add("api-key", key);
                }
                else
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                }
            }

            body = OpenAIBody(model, system, messages, tools);
        }

        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw Failure(response.StatusCode, detail);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ApiStreamReader.ReadAsync(model.Kind, stream, onText, cancellationToken).ConfigureAwait(false);
    }

    private static ApiChatException Failure(HttpStatusCode status, string detail)
    {
        string? message = null;
        try
        {
            message = JsonNode.Parse(detail)?["error"]?["message"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
        }

        var text = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The model's service didn't accept the API key (check it in Ask AI › models).",
            HttpStatusCode.TooManyRequests => "The model's service is busy or you've hit its limit; try again in a moment.",
            HttpStatusCode.NotFound => "The model's service doesn't know that model or address (check the model name and address).",
            _ => $"The model's service answered {(int)status}.",
        };
        return new ApiChatException(message is { Length: > 0 } ? $"{text} ({message[..Math.Min(200, message.Length)]})" : text, (int)status);
    }

    private static JsonObject OpenAIBody(ApiModel model, string system, IReadOnlyList<ApiMessage> messages, IReadOnlyList<ApiToolSpec> tools)
    {
        var list = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case "assistant":
                    var assistant = new JsonObject { ["role"] = "assistant", ["content"] = m.Text };
                    if (m.ToolCalls is { Count: > 0 } calls)
                    {
                        assistant["tool_calls"] = new JsonArray([.. calls.Select(c => (JsonNode)new JsonObject
                        {
                            ["id"] = c.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
                        })]);
                    }

                    list.Add(assistant);
                    break;
                case "tool":
                    list.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId, ["content"] = m.Text ?? string.Empty });
                    break;
                default:
                    list.Add(new JsonObject { ["role"] = "user", ["content"] = m.Text ?? string.Empty });
                    break;
            }
        }

        var body = new JsonObject { ["model"] = model.Model, ["stream"] = true, ["messages"] = list };
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.InputSchema.DeepClone() },
            })]);
        }

        return body;
    }

    private static JsonObject AnthropicBody(ApiModel model, string system, IReadOnlyList<ApiMessage> messages, IReadOnlyList<ApiToolSpec> tools)
    {
        // Anthropic wants user and assistant turns to alternate: tool results travel in a user turn, so a turn with the
        // same role as the one before joins it.
        var list = new JsonArray();
        void Add(string role, params JsonNode[] blocks)
        {
            if (list.Count > 0 && list[^1]!["role"]!.GetValue<string>() == role)
            {
                var content = list[^1]!["content"]!.AsArray();
                foreach (var block in blocks)
                {
                    content.Add(block);
                }
            }
            else
            {
                list.Add(new JsonObject { ["role"] = role, ["content"] = new JsonArray(blocks) });
            }
        }

        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case "assistant":
                    var blocks = new List<JsonNode>();
                    if (m.Text is { Length: > 0 } text)
                    {
                        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                    }

                    foreach (var call in m.ToolCalls ?? [])
                    {
                        blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = call.Id, ["name"] = call.Name, ["input"] = ParseObject(call.Arguments) });
                    }

                    if (blocks.Count > 0)
                    {
                        Add("assistant", [.. blocks]);
                    }

                    break;
                case "tool":
                    Add("user", new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = m.ToolCallId, ["content"] = m.Text ?? string.Empty });
                    break;
                default:
                    Add("user", new JsonObject { ["type"] = "text", ["text"] = m.Text ?? string.Empty });
                    break;
            }
        }

        var body = new JsonObject { ["model"] = model.Model, ["max_tokens"] = MaxOutputTokens, ["stream"] = true, ["system"] = system, ["messages"] = list };
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = t.InputSchema.DeepClone() })]);
        }

        return body;
    }

    private static JsonNode ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }
}
