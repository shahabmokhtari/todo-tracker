using TodoTracker.Server.Plugins.AgentChat;

namespace TodoTracker.Server.Tests.AgentChat;

/// <summary>API models are kept with their keys apart: keys are protected on disk and never handed back out.</summary>
public sealed class ApiModelStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tt-models-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_model_and_its_key_are_kept_but_the_key_is_not_written_in_plain_text()
    {
        var model = ApiModel.Create("openai", "GPT", "https://api.openai.com/v1", "gpt-4.1");
        new ApiModelStore(_dir).Add(model, "sk-secret-123");

        var store = new ApiModelStore(_dir);

        Assert.Equal("gpt-4.1", Assert.Single(store.List()).Model);
        Assert.Equal("sk-secret-123", store.Key(model.Id));
        Assert.True(store.HasKey(model.Id));
        var onDisk = string.Concat(Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("sk-secret-123", File.ReadAllText(Path.Combine(_dir, "models.json")), StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("sk-secret-123", onDisk, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_local_model_needs_no_key()
    {
        var store = new ApiModelStore(_dir);
        var model = ApiModel.Create("ollama", "Llama", "http://127.0.0.1:11434/v1", "llama3.2");

        store.Add(model, null);

        Assert.False(store.HasKey(model.Id));
        Assert.Equal(string.Empty, store.Key(model.Id));
    }

    [Fact]
    public void Removing_a_model_removes_its_key()
    {
        var store = new ApiModelStore(_dir);
        var model = ApiModel.Create("anthropic", "Claude", "https://api.anthropic.com", "claude-sonnet-4-5");
        store.Add(model, "key");

        Assert.True(store.Remove(model.Id));

        Assert.Empty(store.List());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dir, "keys")));
        Assert.False(store.Remove(model.Id));
    }

    [Fact]
    public void Two_models_with_the_same_name_are_both_kept()
    {
        var store = new ApiModelStore(_dir);
        store.Add(ApiModel.Create("openai", "Work", "https://api.openai.com/v1", "gpt-4.1"), "a");
        store.Add(ApiModel.Create("openai", "Work", "https://api.openai.com/v1", "gpt-4.1-mini"), "b");

        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void A_damaged_list_starts_empty_instead_of_failing()
    {
        File.WriteAllText(Path.Combine(_dir, "models.json"), "[ nope");

        Assert.Empty(new ApiModelStore(_dir).List());
    }
}
