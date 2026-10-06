using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TodoTracker.Server.Plugins.AgentChat;

namespace TodoTracker.Server.Tests.AgentChat.Api;

/// <summary>Talking to OpenAI-compatible and Anthropic-compatible APIs: where, how, and what errors mean.</summary>
public sealed class ApiChatClientTests
{
    private sealed class Capture(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public JsonNode? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
        }
    }

    private static readonly IReadOnlyList<ApiToolSpec> Tools =
        [new ApiToolSpec("create_task", "Add a task", JsonNode.Parse("""{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}""")!)];

    private static readonly IReadOnlyList<ApiMessage> Conversation =
    [
        new ApiMessage("user", "Add milk"),
        new ApiMessage("assistant", "Sure.", [new ApiToolCall("c1", "create_task", """{"title":"Milk"}""")]),
        new ApiMessage("tool", "Added \"Milk\".", ToolCallId: "c1", ToolName: "create_task"),
    ];

    private static async Task<Capture> Send(ApiModel model, string key = "sk-test", string body = "data: [DONE]\n\n", HttpStatusCode status = HttpStatusCode.OK)
    {
        var capture = new Capture(status, body);
        using var http = new HttpClient(capture);
        await new ApiChatClient(http).CompleteAsync(model, key, "Be brief.", Conversation, Tools, _ => { }, TestContext.Current.CancellationToken);
        return capture;
    }

    [Fact]
    public async Task OpenAI_gets_chat_completions_with_tools_and_the_whole_conversation()
    {
        var capture = await Send(ApiModel.Create("openai", "GPT", "https://api.openai.com/v1/", "gpt-5"));

        Assert.Equal("https://api.openai.com/v1/chat/completions", capture.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer sk-test", capture.Request.Headers.Authorization!.ToString());
        var body = capture.Body!;
        Assert.Equal("gpt-5", body["model"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal(["system", "user", "assistant", "tool"], body["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal("create_task", body["messages"]![2]!["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("c1", body["messages"]![3]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("create_task", body["tools"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("object", body["tools"]![0]!["function"]!["parameters"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Azure_OpenAI_uses_the_deployment_url_and_an_api_key_header()
    {
        var capture = await Send(ApiModel.Create("azure", "Work GPT", "https://contoso.openai.azure.com", "gpt-5-deployment"));

        Assert.Equal("https://contoso.openai.azure.com/openai/deployments/gpt-5-deployment/chat/completions?api-version=2024-10-21", capture.Request!.RequestUri!.ToString());
        Assert.Equal("sk-test", capture.Request.Headers.GetValues("api-key").Single());
        Assert.Null(capture.Request.Headers.Authorization);
    }

    [Fact]
    public async Task Anthropic_gets_messages_with_tool_use_and_results_in_its_own_shape()
    {
        var capture = await Send(ApiModel.Create("anthropic", "Claude", "https://api.anthropic.com", "claude-sonnet-5"));

        Assert.Equal("https://api.anthropic.com/v1/messages", capture.Request!.RequestUri!.ToString());
        Assert.Equal("sk-test", capture.Request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", capture.Request.Headers.GetValues("anthropic-version").Single());
        var body = capture.Body!;
        Assert.Equal("Be brief.", body["system"]!.GetValue<string>());
        Assert.True(body["max_tokens"]!.GetValue<int>() > 0);
        var messages = body["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user"], messages.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal("tool_use", messages[1]!["content"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("Milk", messages[1]!["content"]![1]!["input"]!["title"]!.GetValue<string>());
        Assert.Equal("tool_result", messages[2]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("c1", messages[2]!["content"]![0]!["tool_use_id"]!.GetValue<string>());
        Assert.Equal("object", body["tools"]![0]!["input_schema"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Local_models_need_no_key()
    {
        var capture = await Send(ApiModel.Create("ollama", "Local", "http://localhost:11434/v1", "llama3.2"), key: string.Empty);

        Assert.Equal("http://localhost:11434/v1/chat/completions", capture.Request!.RequestUri!.ToString());
        Assert.Null(capture.Request.Headers.Authorization);
    }

    [Theory]
    [InlineData("http://api.example.com/v1")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com")]
    [InlineData("not a url")]
    [InlineData("https://user:pass@api.example.com/v1")]
    public void Only_https_or_this_computer_is_accepted(string url)
    {
        // Task details go to this address: plain http only for models running on this computer.
        Assert.Throws<ArgumentException>(() => ApiModel.Create("openai", "X", url, "m"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "API key")]
    [InlineData(HttpStatusCode.TooManyRequests, "busy")]
    [InlineData(HttpStatusCode.NotFound, "model")]
    public async Task Errors_say_what_to_do(HttpStatusCode status, string hint)
    {
        var error = await Assert.ThrowsAsync<ApiChatException>(() => Send(ApiModel.Create("openai", "GPT", "https://api.openai.com/v1", "gpt-5"), status: status, body: """{"error":{"message":"nope"}}"""));

        Assert.Contains(hint, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((int)status, error.Status);
    }

    [Theory]
    [InlineData("This model's maximum context length is 128000 tokens. However, your messages resulted in 130000 tokens.", true)]
    [InlineData("prompt is too long: 210000 tokens > 200000 maximum", true)]
    [InlineData("max_tokens is too large: 50000. This model supports at most 16384 completion tokens, maximum allowed.", false)]
    [InlineData("tools: maximum of 128 tools exceeded", false)]
    public async Task Only_a_conversation_that_is_too_long_suggests_a_new_chat(string message, bool tooLong)
    {
        var error = await Assert.ThrowsAsync<ApiChatException>(() => Send(ApiModel.Create("openai", "GPT", "https://api.openai.com/v1", "gpt-5"), status: HttpStatusCode.BadRequest, body: new JsonObject { ["error"] = new JsonObject { ["message"] = message } }.ToJsonString()));

        Assert.Equal(tooLong, error.Message.Contains("new chat", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("registry.ollama.ai/library/gemma3 does not support tools", true)]
    [InlineData("tools.0.custom.input_schema: JSON schema is invalid. Format 'uri' is not supported", false)]
    public void Only_a_model_without_tools_is_asked_again_without_them(string message, bool noTools) =>
        Assert.Equal(noTools, ApiChatClient.NoTools().IsMatch(message));
}
