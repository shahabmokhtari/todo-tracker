using System.Text;
using TodoTracker.Server.Plugins.AgentChat;

namespace TodoTracker.Server.Tests.AgentChat.Api;

/// <summary>Streaming answers from OpenAI- and Anthropic-style APIs, piece by piece, into text and tool calls.</summary>
public sealed class ApiStreamReaderTests
{
    private static async Task<(string Text, List<ApiToolCall> Calls, string? Stop)> Read(ApiKind kind, string sse)
    {
        var text = new StringBuilder();
        var turn = await ApiStreamReader.ReadAsync(kind, new MemoryStream(Encoding.UTF8.GetBytes(sse)), t => text.Append(t), TestContext.Current.CancellationToken);
        return (text.ToString(), [.. turn.ToolCalls], turn.StopReason);
    }

    [Fact]
    public async Task OpenAI_text_streams_in_pieces()
    {
        var sse = """
            data: {"choices":[{"delta":{"role":"assistant","content":"Hel"}}]}

            : keep-alive

            data: {"choices":[{"delta":{"content":"lo."}}]}

            data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;

        var (text, calls, stop) = await Read(ApiKind.OpenAI, sse);

        Assert.Equal("Hello.", text);
        Assert.Empty(calls);
        Assert.Equal("stop", stop);
    }

    [Fact]
    public async Task OpenAI_tool_calls_arrive_in_pieces_and_in_parallel()
    {
        var sse = """
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"create_task","arguments":""}}]}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_b","type":"function","function":{"name":"get_dashboard","arguments":"{}"}}]}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"title\":\"Buy"}}]}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":" milk\"}"}}]}}]}

            data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;

        var (_, calls, stop) = await Read(ApiKind.OpenAI, sse);

        Assert.Equal("tool_calls", stop);
        Assert.Equal([("call_a", "create_task", """{"title":"Buy milk"}"""), ("call_b", "get_dashboard", "{}")], calls.Select(c => (c.Id, c.Name, c.Arguments)));
    }

    [Fact]
    public async Task Anthropic_text_and_tool_use_stream_as_content_blocks()
    {
        var sse = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","role":"assistant"}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"On it"}}

            event: ping
            data: {"type":"ping"}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"create_task","input":{}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"title\":"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\"Buy milk\"}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":1}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"}}

            event: message_stop
            data: {"type":"message_stop"}

            """;

        var (text, calls, stop) = await Read(ApiKind.Anthropic, sse);

        Assert.Equal("On it", text);
        Assert.Equal("tool_use", stop);
        var call = Assert.Single(calls);
        Assert.Equal(("toolu_1", "create_task", """{"title":"Buy milk"}"""), (call.Id, call.Name, call.Arguments));
    }

    [Fact]
    public async Task A_tool_with_no_arguments_gets_an_empty_object()
    {
        var sse = """
            data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_2","name":"get_dashboard","input":{}}}

            data: {"type":"content_block_stop","index":0}

            data: {"type":"message_stop"}

            """;

        var (_, calls, _) = await Read(ApiKind.Anthropic, sse);

        Assert.Equal("{}", Assert.Single(calls).Arguments);
    }

    [Fact]
    public async Task An_error_in_the_stream_is_reported()
    {
        var sse = """
            event: error
            data: {"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}

            """;

        var error = await Assert.ThrowsAsync<ApiChatException>(() => Read(ApiKind.Anthropic, sse));
        Assert.Contains("Overloaded", error.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ApiChatException>(() => Read(ApiKind.OpenAI, """data: {"error":{"message":"bad key"}}""" + "\n\n"));
    }

    [Fact]
    public async Task Lines_split_across_reads_and_CRLF_line_ends_are_fine()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\r\n\r\ndata: {\"choices\":[{\"delta\":{\"content\":\"b\"}}]}\r\n\r\ndata: [DONE]\r\n\r\n";

        var text = new StringBuilder();
        await ApiStreamReader.ReadAsync(ApiKind.OpenAI, new TrickleStream(Encoding.UTF8.GetBytes(sse)), t => text.Append(t), TestContext.Current.CancellationToken);

        Assert.Equal("ab", text.ToString());
    }

    /// <summary>Hands out a few bytes at a time, like a slow network.</summary>
    private sealed class TrickleStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 3));
    }
}
