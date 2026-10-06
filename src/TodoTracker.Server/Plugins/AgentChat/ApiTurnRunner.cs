namespace TodoTracker.Server.Plugins.AgentChat;

/// <summary>
/// How far one answer may go: model requests (rounds), tool calls, how much of each tool result the model sees, and how
/// much of the conversation is sent (the oldest turns are left out beyond it; everything is still kept).
/// </summary>
public sealed record ApiTurnLimits(int MaxRounds = 12, int MaxToolCalls = 24, int MaxResultChars = 16_000, int MaxContextChars = 120_000);

/// <summary>What an answer does as it happens, for the chat to show (and to ask before changes).</summary>
public interface IApiTurnEvents
{
    void Text(string chunk);

    void ToolStarted(string callId, string name, string arguments);

    /// <summary><paramref name="status"/>: completed, failed, or rejected.</summary>
    void ToolFinished(string callId, string status);

    /// <summary>Asks before a tool that changes tasks; true to go ahead.</summary>
    Task<bool> AllowAsync(string name, string arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Runs one answer from an API model: send the conversation, stream the text, run the tools it asks for (reading
/// freely, changing only with permission, nothing it wasn't offered), send the results back, and repeat until it's done
/// or a limit is reached. Tool calls are never retried. Whatever happens, every tool call in the conversation gets a
/// result, so the conversation can always go on.
/// </summary>
public sealed class ApiTurnRunner(ApiChatClient client, ApiTurnLimits limits)
{
    public async Task<string> RunAsync(ApiModel model, string key, string system, List<ApiMessage> messages, IChatTools tools, IApiTurnEvents events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(events);
        var offered = await tools.ListAsync(cancellationToken).ConfigureAwait(false);
        var calls = 0;
        for (var round = 1; round <= limits.MaxRounds; round++)
        {
            ApiTurn turn;
            try
            {
                turn = await client.CompleteAsync(model, key, system, Window(messages), offered, events.Text, cancellationToken).ConfigureAwait(false);
            }
            catch (ApiChatException ex) when (offered.Count > 0 && ex.Status == 400 && ex.Message.Contains("tools", StringComparison.OrdinalIgnoreCase) && ex.Message.Contains("support", StringComparison.OrdinalIgnoreCase))
            {
                // Some local models can't use tools at all: they can still talk.
                offered = [];
                turn = await client.CompleteAsync(model, key, system, Window(messages), offered, events.Text, cancellationToken).ConfigureAwait(false);
            }

            if (turn.Text.Length == 0 && turn.ToolCalls.Count == 0)
            {
                // Nothing said: an empty message would make the API refuse every later request in this chat.
                return Ended(turn.StopReason) is "end_turn" ? "empty" : Ended(turn.StopReason);
            }

            messages.Add(new ApiMessage("assistant", turn.Text.Length > 0 ? turn.Text : null, turn.ToolCalls.Count > 0 ? turn.ToolCalls : null));
            if (turn.ToolCalls.Count == 0)
            {
                return Ended(turn.StopReason);
            }

            var answered = 0;
            try
            {
                foreach (var call in turn.ToolCalls)
                {
                    var result = ++calls > limits.MaxToolCalls
                        ? "Not run: too many tool calls in one answer. Tell the person what's left to do."
                        : await RunToolAsync(call, offered, tools, events, cancellationToken).ConfigureAwait(false);
                    messages.Add(new ApiMessage("tool", result, ToolCallId: call.Id, ToolName: call.Name));
                    answered++;
                }
            }
            finally
            {
                // Stopped part-way: the calls not run still get an answer.
                foreach (var call in turn.ToolCalls.Skip(answered))
                {
                    messages.Add(new ApiMessage("tool", "Not run: the person stopped the answer.", ToolCallId: call.Id, ToolName: call.Name));
                }
            }
        }

        return "max_turn_requests";
    }

    /// <summary>The stop reasons of both APIs, as the chat words them (like ACP's).</summary>
    private static string Ended(string? reason) => reason switch
    {
        "length" or "max_tokens" => "max_tokens",
        "content_filter" or "refusal" => "refusal",
        _ => "end_turn",
    };

    /// <summary>
    /// The recent part of the conversation that fits the budget: whole turns only (from a person's message on), so a
    /// tool call always travels with its result. The latest message always goes.
    /// </summary>
    private List<ApiMessage> Window(List<ApiMessage> messages)
    {
        static int Size(ApiMessage m) => (m.Text?.Length ?? 0) + (m.ToolCalls?.Sum(c => c.Arguments.Length + c.Name.Length) ?? 0) + 20;
        var total = 0;
        var start = messages.Count;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            total += Size(messages[i]);
            if (messages[i].Role != "user")
            {
                continue;
            }

            if (total > limits.MaxContextChars && start < messages.Count)
            {
                break;
            }

            start = i;
        }

        return start == 0 || start == messages.Count ? messages : messages[start..];
    }

    private async Task<string> RunToolAsync(ApiToolCall call, IReadOnlyList<ApiToolSpec> offered, IChatTools tools, IApiTurnEvents events, CancellationToken cancellationToken)
    {
        var spec = offered.FirstOrDefault(t => t.Name == call.Name);
        if (spec is not null && !spec.ReadOnly && !await events.AllowAsync(call.Name, call.Arguments, cancellationToken).ConfigureAwait(false))
        {
            events.ToolStarted(call.Id, call.Name, call.Arguments);
            events.ToolFinished(call.Id, "rejected");
            return "The person declined this change. Don't try it again unless they ask.";
        }

        events.ToolStarted(call.Id, call.Name, call.Arguments);
        if (spec is null)
        {
            events.ToolFinished(call.Id, "failed");
            return $"There's no tool called \"{call.Name}\". Use only the tools offered.";
        }

        var result = await tools.CallAsync(call.Name, call.Arguments, cancellationToken).ConfigureAwait(false);
        events.ToolFinished(call.Id, result.IsError ? "failed" : "completed");
        var text = result.IsError ? "Error: " + result.Text : result.Text;
        return text.Length <= limits.MaxResultChars
            ? text
            : text[..limits.MaxResultChars] + $"\n[… cut: {text.Length - limits.MaxResultChars} more characters. Ask for less, e.g. one task.]";
    }
}
