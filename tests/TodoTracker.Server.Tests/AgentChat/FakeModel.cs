using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TodoTracker.Server.Plugins.AgentChat;

namespace TodoTracker.Server.Tests.AgentChat;

/// <summary>An OpenAI-compatible service that answers with scripted streams, one per request, and keeps what it was sent.</summary>
internal sealed class FakeModel : HttpMessageHandler
{
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _answers = new();

    public List<JsonNode> Requests { get; } = [];

    public static ApiModel Model { get; } = ApiModel.Create("openai", "Fake GPT", "https://models.example/v1", "fake-1", "fakemodel01");

    /// <summary>Answers with text (streamed in two pieces).</summary>
    public FakeModel Says(string text)
    {
        var half = text.Length / 2;
        return Streams(Chunk(new JsonObject { ["content"] = text[..half] }), Chunk(new JsonObject { ["content"] = text[half..] }), Finish("stop"));
    }

    /// <summary>Answers by calling tools.</summary>
    public FakeModel Calls(params (string Id, string Name, string Arguments)[] calls) =>
        Streams([.. calls.Select((c, i) => Chunk(new JsonObject
        {
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["index"] = i, ["id"] = c.Id, ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
            }),
        })), Finish("tool_calls")]);

    public FakeModel Fails(HttpStatusCode status, string message)
    {
        _answers.Enqueue(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(new JsonObject { ["error"] = new JsonObject { ["message"] = message } }.ToJsonString()) }));
        return this;
    }

    /// <summary>Never answers until cancelled.</summary>
    public FakeModel Hangs()
    {
        _answers.Enqueue(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(JsonNode.Parse(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult())!);
        }

        Func<CancellationToken, Task<HttpResponseMessage>> answer;
        lock (_answers)
        {
            answer = _answers.Count > 0 ? _answers.Dequeue() : _ => Task.FromResult(Sse(Chunk(new JsonObject { ["content"] = "(no more answers)" }), Finish("stop")));
        }

        return await answer(cancellationToken);
    }

    private FakeModel Streams(params string[] events)
    {
        _answers.Enqueue(_ => Task.FromResult(Sse(events)));
        return this;
    }

    private static HttpResponseMessage Sse(params string[] events) =>
        new(HttpStatusCode.OK) { Content = new StringContent(string.Concat(events.Select(e => $"data: {e}\n\n")) + "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };

    private static string Chunk(JsonObject delta) => new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = delta }) }.ToJsonString();

    private static string Finish(string reason) => new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject(), ["finish_reason"] = reason }) }.ToJsonString();
}

/// <summary>Task tools that record their calls: get_dashboard reads, create_task changes.</summary>
internal sealed class FakeTools : IChatTools
{
    public List<(string Name, string Arguments)> Calls { get; } = [];

    public string DashboardText { get; set; } = "3 things to do";

    public bool Disposed { get; private set; }

    public Task<IReadOnlyList<ApiToolSpec>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ApiToolSpec>>(
        [
            new ApiToolSpec("get_dashboard", "What to do now", JsonNode.Parse("""{"type":"object"}""")!, ReadOnly: true),
            new ApiToolSpec("create_task", "Add a task", JsonNode.Parse("""{"type":"object","properties":{"title":{"type":"string"}}}""")!),
        ]);

    public Task<ChatToolResult> CallAsync(string name, string arguments, CancellationToken cancellationToken)
    {
        Calls.Add((name, arguments));
        return Task.FromResult(name == "get_dashboard" ? new ChatToolResult(DashboardText, false) : new ChatToolResult($"Added {arguments}", false));
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
