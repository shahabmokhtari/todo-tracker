// A scripted ACP agent for tests. It speaks JSON-RPC over stdio like `copilot --acp --stdio` and reacts to words in
// the prompt: "crash", "slow" (waits for session/cancel), "add <title>" (asks permission to edit), "read" (asks
// permission for a read-only tool), "shell" (asks to run a command), "spawn" (starts a child process), "stderr"
// (floods stderr), anything else (a two-part greeting). Every message is written in two pieces to test framing.
// FAKE_ACP_LOG=<file> appends every message it receives.
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

async Task<JsonNode?> Ask(string sessionId, string title, string kind, JsonObject? rawInput = null)
{
    var id = Interlocked.Increment(ref nextId);
    var answer = new TaskCompletionSource<JsonNode?>();
    lock (pending)
    {
        pending[id] = answer;
    }

    var toolCall = new JsonObject { ["toolCallId"] = $"call{id}", ["title"] = title, ["kind"] = kind, ["status"] = "pending" };
    if (rawInput is not null)
    {
        toolCall["rawInput"] = rawInput.DeepClone();
    }

    await Update(sessionId, new JsonObject { ["sessionUpdate"] = "tool_call", ["toolCallId"] = $"call{id}", ["title"] = title, ["kind"] = kind, ["status"] = "pending" });
    await Send(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "session/request_permission",
        ["params"] = new JsonObject
        {
            ["sessionId"] = sessionId,
            ["toolCall"] = toolCall,
            ["options"] = new JsonArray(
                new JsonObject { ["optionId"] = "allow-once", ["name"] = "Allow once", ["kind"] = "allow_once" },
                new JsonObject { ["optionId"] = "allow-always", ["name"] = "Always allow", ["kind"] = "allow_always" },
                new JsonObject { ["optionId"] = "reject-once", ["name"] = "Reject", ["kind"] = "reject_once" }),
        },
    });
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

    if (text.IndexOf("add ", StringComparison.OrdinalIgnoreCase) is var at and >= 0)
    {
        var title = text[(at + 4)..].Trim();
        var answer = await Ask(sessionId, "todo-tracker: create_task", "edit", new JsonObject { ["title"] = title });
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
        await Ask(sessionId, "todo-tracker: get_dashboard", "read");
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
                    ["agentCapabilities"] = new JsonObject { ["loadSession"] = false },
                    ["agentInfo"] = new JsonObject { ["name"] = "fake-agent", ["version"] = "1.0" },
                    ["authMethods"] = new JsonArray(),
                },
            });
            break;
        case "session/new":
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
