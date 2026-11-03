using System.Text.Json.Nodes;

namespace OneThing.Core.Tests;

/// <summary>Send feedback: tests/fixtures/feedback.json is the same request body and the same answers in C#, Swift, Kotlin and the web app.</summary>
public sealed class FeedbackFixtureTests
{
    private static readonly JsonObject Fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "feedback.json")))!.AsObject();

    public static TheoryData<string> Cases() => [.. Fixture["bodies"]!.AsArray().Select(c => c!["name"]!.GetValue<string>())];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_app_sends_the_body_the_shared_fixture_says(string name)
    {
        var c = Fixture["bodies"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == name)!;
        var d = c["draft"]!;
        var v = c["device"]!;
        var draft = new FeedbackDraft(d["kind"]!.GetValue<string>(), d["message"]?.GetValue<string>(), d["contact"]?.GetValue<string>(), d["source"]!.GetValue<string>(), d["clientID"]!.GetValue<string>());
        var device = new FeedbackDevice(v["platform"]!.GetValue<string>(), v["appVersion"]?.GetValue<string>(), v["build"]?.GetValue<string>(),
            v["os"]?.GetValue<string>(), v["device"]?.GetValue<string>(), v["language"]?.GetValue<string>(), v["screen"]?.GetValue<string>());
        var max = c["maxMessage"]?.GetValue<int>() ?? Feedback.MaxMessage;

        var body = Feedback.Body(draft, device, max);

        if (c["body"] is not JsonObject expected)
        {
            Assert.Null(body);
            Assert.Equal(string.Empty, Feedback.CleanMessage(draft.Message, max));
            return;
        }

        Assert.NotNull(body);
        Assert.True(JsonNode.DeepEquals(expected, body), $"{expected.ToJsonString()}\n{body.ToJsonString()}");
        Assert.Equal(expected.Select(p => p.Key), body.Select(p => p.Key));
        Assert.Equal(expected["metadata"]!.AsObject().Select(p => p.Key), body["metadata"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public void Each_answer_from_the_relay_means_what_the_shared_fixture_says()
    {
        foreach (var o in Fixture["outcomes"]!.AsArray())
        {
            var status = o!["status"]?.GetValue<int>();
            Assert.Equal(o["outcome"]!.GetValue<string>(), Feedback.Classify(status).ToString().ToLowerInvariant());
        }
    }

    [Fact]
    public void The_relay_its_key_the_kinds_and_the_words_are_the_shared_ones()
    {
        var relay = Fixture["relay"]!;
        Assert.Equal(Feedback.RelayUrl, relay["url"]!.GetValue<string>());
        Assert.Equal(Feedback.PublicKey, relay["publicKey"]!.GetValue<string>());
        Assert.Equal(Feedback.MaxMessage, relay["maxMessage"]!.GetValue<int>());
        Assert.Equal(Feedback.MaxContact, relay["maxContact"]!.GetValue<int>());
        Assert.Equal(Fixture["kinds"]!.AsArray().Select(k => (k!["id"]!.GetValue<string>(), k["label"]!.GetValue<string>())), Feedback.Kinds);
        Assert.Equal(Fixture["sources"]!.AsArray().Select(s => s!.GetValue<string>()), Feedback.Sources);

        var texts = Fixture["texts"]!;
        Assert.Equal(Feedback.Texts.Sent, texts["sent"]!.GetValue<string>());
        Assert.Equal(Feedback.Texts.Retry, texts["retry"]!.GetValue<string>());
        Assert.Equal(Feedback.Texts.Failed, texts["failed"]!.GetValue<string>());
        Assert.Equal(Feedback.Texts.FinePrint, texts["finePrint"]!.GetValue<string>());
        Assert.Equal(Feedback.Texts.ContactHint, texts["contactHint"]!.GetValue<string>());
    }

    [Fact]
    public void An_unknown_kind_or_source_is_refused()
    {
        var device = new FeedbackDevice("Web", "1.0.0", null, null, null, null, null);
        Assert.Throws<ArgumentException>(() => Feedback.Body(new FeedbackDraft("rant", "Hi", null, "button", Guid.NewGuid().ToString()), device));
        Assert.Throws<ArgumentException>(() => Feedback.Body(new FeedbackDraft("bug", "Hi", null, "email", Guid.NewGuid().ToString()), device));
    }
}
