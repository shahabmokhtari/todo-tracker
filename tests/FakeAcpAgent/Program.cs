// A scripted ACP agent for tests. It speaks JSON-RPC over stdio like `copilot --acp --stdio` and reacts to words in
// the prompt: "crash", "slow" (waits for session/cancel), "add <title>" (asks permission to edit, named the way
// Copilot names our tools), "read" (a read-only tool), "shell" (asks to run a command), "sneaky" (a command that
// mentions our tool names), "imposter" (another server's tool with our tool's name), "claude" (Claude's tool naming),
// "badask" (a request without choices), "garbage" (odd message shapes), "elsewhere" (an update for another session),
// "spawn" (starts a child process), "stderr" (floods stderr), anything else (a two-part greeting). Every message is
// written in two pieces to test framing. FAKE_ACP_LOG=<file> appends every message it receives;
// FAKE_ACP_NO_SESSION=1 answers session/new without a session id.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };
var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
var log = Environment.GetEnvironmentVariable("FAKE_ACP_LOG");
var gate = new SemaphoreSlim(1, 1);
var pending = new Dictionary<int, TaskCompletionSource<JsonNode?>>();
var cancelled = new TaskCompletionSource();
var nextId = 1000;
var sessions = 0;

async Task Send(JsonObject message)
{
    var text = message.ToJsonString() + "\n";
    await gate.WaitAsync();
    try
    {
        var half = text.Length / 2;
        await stdout.WriteAsync(text[..half]);
        await stdout.FlushAsync();
        await stdout.WriteAsync(text[half..]);
        await stdout.FlushAsync();
    }
    finally
    {
        gate.Release();
    }
}

Task Update(string sessionId, JsonObject update) =>
    Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "session/update", ["params"] = new JsonObject { ["sessionId"] = sessionId, ["update"] = update } });

Task Chunk(string sessionId, string text, string messageId = "m1") =>
    Update(sessionId, new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["messageId"] = messageId, ["content"] = new JsonObject { ["type"] = "text", ["text"] = text } });

async Task<JsonNode?> Ask(string sessionId, string title, string kind, JsonObject? rawInput = null, string? announced = null, string? announcedKind = null, bool withOptions = true)
{
    var id = Interlocked.Increment(ref nextId);
    var answer = new TaskCompletionSource<JsonNode?>();
    lock (pending)
    {
        pending[id] = answer;
    }

    // Like Copilot: the tool_call update names the tool with its server ("todo-tracker-create_task"); the permission
    // request for the same toolCallId carries only the bare tool name and kind "other".
    var toolCall = new JsonObject { ["toolCallId"] = $"call{id}", ["title"] = title, ["kind"] = kind, ["status"] = "pending" };
    if (rawInput is not null)
    {
        toolCall["rawInput"] = rawInput.DeepClone();
    }

    await Update(sessionId, new JsonObject { ["sessionUpdate"] = "tool_call", ["toolCallId"] = $"call{id}", ["title"] = announced ?? title, ["kind"] = announcedKind ?? kind, ["status"] = "pending" });
    var request = new JsonObject { ["sessionId"] = sessionId, ["toolCall"] = toolCall };
    if (withOptions)
    {
        request["options"] = new JsonArray(
            new JsonObject { ["optionId"] = "allow-once", ["name"] = "Allow once", ["kind"] = "allow_once" },
            new JsonObject { ["optionId"] = "allow-always", ["name"] = "Always allow", ["kind"] = "allow_always" },
            new JsonObject { ["optionId"] = "reject-once", ["name"] = "Reject", ["kind"] = "reject_once" });
    }

    await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "session/request_permission", ["params"] = request });
    var result = await answer.Task;
    var outcome = result?["outcome"];
    var allowed = outcome?["outcome"]?.GetValue<string>() == "selected" && outcome["optionId"]?.GetValue<string>()?.StartsWith("allow", StringComparison.Ordinal) == true;
    await Update(sessionId, new JsonObject { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = $"call{id}", ["status"] = allowed ? "completed" : "failed" });
    return result;
}

async Task<string> Prompt(string sessionId, string text)
{
    if (text.Contains("crash", StringComparison.OrdinalIgnoreCase))
    {
        Environment.Exit(3);
    }

    if (text.Contains("stderr", StringComparison.OrdinalIgnoreCase))
    {
        var noise = new string('x', 1024);
        for (var i = 0; i < 300; i++)
        {
            await Console.Error.WriteLineAsync(noise);
        }
    }

    if (text.Contains("slow", StringComparison.OrdinalIgnoreCase))
    {
        await Chunk(sessionId, "Working on it…");
        await cancelled.Task;
        return "cancelled";
    }

    if (text.Contains("sneaky", StringComparison.OrdinalIgnoreCase))
    {
        // A shell command that mentions one of Todo Tracker's tool names must still ask.
        var answer = await Ask(sessionId, "Run shell command", "execute", new JsonObject { ["command"] = "Remove-Item -Recurse $HOME\\Documents  # list_tasks get_dashboard" });
        await Chunk(sessionId, answer?["outcome"]?["optionId"]?.GetValue<string>() ?? "no answer");
        return "end_turn";
    }

    if (text.Contains("mismatch", StringComparison.OrdinalIgnoreCase))
    {
        // The request claims to be one of our tools, but the call was announced as something else.
        var answer = await Ask(sessionId, "todo-tracker-list_tasks", "other", announced: "Run Remove-Item -Recurse $HOME", announcedKind: "other");
        await Chunk(sessionId, answer?["outcome"]?["optionId"]?.GetValue<string>() ?? "no answer");
        return "end_turn";
    }

    if (text.Contains("stubborn", StringComparison.OrdinalIgnoreCase))
    {
        // Ignores session/cancel: only stopping the agent ends this.
        await Chunk(sessionId, "Not stopping.");
        await Task.Delay(Timeout.Infinite);
    }

    if (text.Contains("imposter", StringComparison.OrdinalIgnoreCase))
    {
        // Another MCP server's tool with the same name as ours is not ours.
        var answer = await Ask(sessionId, "create_task", "other", announced: "other-server-create_task", announcedKind: "edit");
        await Chunk(sessionId, answer?["outcome"]?["optionId"]?.GetValue<string>() ?? "no answer");
        return "end_turn";
    }

    if (text.Contains("claude", StringComparison.OrdinalIgnoreCase))
    {
        // Claude Code names MCP tools mcp__<server>__<tool>.
        var answer = await Ask(sessionId, "mcp__todo-tracker__get_dashboard", "other");
        await Chunk(sessionId, answer?["outcome"]?["optionId"]?.GetValue<string>() ?? "no answer");
        return "end_turn";
    }

    if (text.Contains("badask", StringComparison.OrdinalIgnoreCase))
    {
        var answer = await Ask(sessionId, "create_task", "other", announced: "todo-tracker-create_task", withOptions: false);
        await Chunk(sessionId, answer is null ? "refused" : "answered");
        return "end_turn";
    }

    if (text.Contains("garbage", StringComparison.OrdinalIgnoreCase))
    {
        // Odd shapes must not stop the client from reading the rest.
        await Update(sessionId, new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["messageId"] = 42, ["content"] = new JsonObject { ["type"] = "text", ["text"] = 7 } });
        await Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "session/update", ["params"] = new JsonObject { ["sessionId"] = sessionId, ["update"] = "nope" } });
        await Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "session/update", ["params"] = 5 });
        await Chunk(sessionId, "still here");
        return "end_turn";
    }

    if (text.Contains("elsewhere", StringComparison.OrdinalIgnoreCase))
    {
        await Chunk("another-session", "LEAK");
        await Chunk(sessionId, "ok");
        return "end_turn";
    }

    if (text.IndexOf("add ", StringComparison.OrdinalIgnoreCase) is var at and >= 0)
    {
        var title = text[(at + 4)..].Trim();
        var answer = await Ask(sessionId, "create_task", "other", new JsonObject { ["title"] = title }, announced: "todo-tracker-create_task", announcedKind: "edit");
        var outcome = answer?["outcome"];
        if (outcome?["outcome"]?.GetValue<string>() == "cancelled")
        {
            return "cancelled";
        }

        var allowed = outcome?["optionId"]?.GetValue<string>()?.StartsWith("allow", StringComparison.Ordinal) == true;
        await Chunk(sessionId, allowed ? $"Added \"{title}\"." : "Okay, I didn't add it.");
        return "end_turn";
    }

    if (text.Contains("read", StringComparison.OrdinalIgnoreCase))
    {
        await Ask(sessionId, "get_dashboard", "other", announced: "todo-tracker-get_dashboard", announcedKind: "read");
        await Chunk(sessionId, "You have 3 things to do.");
        return "end_turn";
    }

    if (text.Contains("shell", StringComparison.OrdinalIgnoreCase))
    {
        var answer = await Ask(sessionId, "Run: Remove-Item -Recurse C:\\", "execute");
        await Chunk(sessionId, answer?["outcome"]?["optionId"]?.GetValue<string>() ?? "no answer");
        return "end_turn";
    }

    if (text.Contains("spawn", StringComparison.OrdinalIgnoreCase))
    {
        var child = Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "ping" : "sleep", OperatingSystem.IsWindows() ? "-n 600 127.0.0.1" : "600") { UseShellExecute = false, RedirectStandardOutput = true })!;
        await Chunk(sessionId, $"child {child.Id}");
        return "end_turn";
    }

    await Chunk(sessionId, "Hello, ");
    await Chunk(sessionId, "there.");
    return "end_turn";
}

while (await stdin.ReadLineAsync() is { } line)
{
    if (line.Length == 0)
    {
        continue;
    }

    if (log is not null)
    {
        await File.AppendAllTextAsync(log, line + "\n");
    }

    JsonNode? message;
    try
    {
        message = JsonNode.Parse(line);
    }
    catch (JsonException)
    {
        continue;
    }

    var method = message?["method"]?.GetValue<string>();
    var id = message?["id"];
    if (method is null && id is not null)
    {
        // A response to one of our requests (a permission answer).
        TaskCompletionSource<JsonNode?>? waiting;
        lock (pending)
        {
            pending.Remove(id.GetValue<int>(), out waiting);
        }

        waiting?.TrySetResult(message!["result"]);
        continue;
    }

    var @params = message?["params"];
    switch (method)
    {
        case "initialize":
            await Send(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id!.DeepClone(),
                ["result"] = new JsonObject
                {
                    ["protocolVersion"] = 1,
                    // FAKE_ACP_HTTP=1 behaves like Copilot: MCP servers over HTTP only.
                    ["agentCapabilities"] = Environment.GetEnvironmentVariable("FAKE_ACP_HTTP") == "1"
                        ? new JsonObject { ["loadSession"] = false, ["mcpCapabilities"] = new JsonObject { ["http"] = true, ["sse"] = true } }
                        : new JsonObject { ["loadSession"] = false },
                    ["agentInfo"] = new JsonObject { ["name"] = "fake-agent", ["version"] = "1.0" },
                    ["authMethods"] = new JsonArray(),
                },
            });
            break;
        case "session/new":
            // FAKE_ACP_CRASH_ONCE=<file>: crash while starting the first session (like Copilot sometimes does).
            if (Environment.GetEnvironmentVariable("FAKE_ACP_CRASH_ONCE") is { } marker && !File.Exists(marker))
            {
                await File.WriteAllTextAsync(marker, "crashed");
                Environment.Exit(unchecked((int)0xC0000005));
            }

            if (Environment.GetEnvironmentVariable("FAKE_ACP_NO_SESSION") == "1")
            {
                await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id!.DeepClone(), ["result"] = new JsonObject() });
                break;
            }

            await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id!.DeepClone(), ["result"] = new JsonObject { ["sessionId"] = $"sess-{++sessions}" } });
            break;
        case "session/prompt":
            var sessionId = @params!["sessionId"]!.GetValue<string>();
            // React to the person's words (the last block), not to context the client put before them.
            var text = @params["prompt"]!.AsArray().LastOrDefault()?["text"]?.GetValue<string>() ?? string.Empty;
            var requestId = id!.DeepClone();
            cancelled = new TaskCompletionSource();
            _ = Task.Run(async () =>
            {
                var stop = await Prompt(sessionId, text);
                await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = requestId, ["result"] = new JsonObject { ["stopReason"] = stop } });
            });
            break;
        case "session/cancel":
            cancelled.TrySetResult();
            lock (pending)
            {
                foreach (var waiting in pending.Values)
                {
                    waiting.TrySetResult(new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } });
                }

                pending.Clear();
            }

            break;
        default:
            if (id is not null)
            {
                await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Unknown method {method}" } });
            }

            break;
    }
}
