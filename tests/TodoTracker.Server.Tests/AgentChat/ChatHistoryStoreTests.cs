using Microsoft.Extensions.Time.Testing;
using TodoTracker.Server.Plugins.AgentChat;

namespace TodoTracker.Server.Tests.AgentChat;

/// <summary>Chats are kept, one file each, so they can be reopened, searched, renamed and deleted (with undo).</summary>
public sealed class ChatHistoryStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tt-chats-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ChatHistoryStore Store() => new(_dir, _time);

    private ChatRecord Chat(string firstMessage, string agent = "copilot")
    {
        var chat = ChatRecord.New(agent, _time.GetUtcNow());
        chat.Entries.Add(new ChatEntry("e1", "user", firstMessage));
        chat.Entries.Add(new ChatEntry("e2", "agent", "Sure."));
        return chat;
    }

    [Fact]
    public void A_late_save_never_brings_back_a_deleted_chat()
    {
        var store = Store();
        var chat = Chat("gone");
        store.Save(chat);
        store.Delete(chat.Id);

        chat.Entries.Add(new ChatEntry("e3", "agent", "a late answer"));
        store.Save(chat);

        Assert.Empty(store.List());
        Assert.True(store.Restore(chat.Id));
    }

    [Fact]
    public void A_question_still_waiting_is_saved_as_no_longer_waiting()
    {
        // Reopened later, a chat must not offer buttons for a question nobody is waiting on any more.
        var chat = Chat("add milk");
        chat.Entries.Add(new ChatEntry("e3", "permission", "Change your tasks: create task", "waiting", ["allow", "reject"]));
        Store().Save(chat);

        var question = Store().Load(chat.Id)!.Entries[^1];

        Assert.Equal("cancelled", question.Status);
        Assert.Null(question.Choices);
        Assert.Equal("waiting", chat.Entries[^1].Status);
    }

    [Fact]
    public void A_saved_chat_comes_back_whole_after_a_restart()
    {
        var chat = Chat("Plan my week");
        chat.AcpSessionId = "sess-1";
        chat.Messages.Add(new ApiMessage("user", "Plan my week"));
        chat.Messages.Add(new ApiMessage("assistant", null, [new ApiToolCall("c1", "list_tasks", "{}")]));
        Store().Save(chat);

        var loaded = Store().Load(chat.Id)!;

        Assert.Equal("Plan my week", loaded.Title);
        Assert.Equal(["user", "agent"], loaded.Entries.Select(e => e.Kind));
        Assert.Equal("sess-1", loaded.AcpSessionId);
        Assert.Equal("list_tasks", loaded.Messages[1].ToolCalls![0].Name);
        Assert.Equal("copilot", Assert.Single(Store().List()).Agent);
    }

    [Fact]
    public void The_title_is_the_first_message_shortened_until_renamed()
    {
        var store = Store();
        var chat = Chat("Help me break down the quarterly report into small steps I can start today please");
        store.Save(chat);
        Assert.Equal(60, store.List()[0].Title.Length);
        Assert.EndsWith("…", store.List()[0].Title, StringComparison.Ordinal);

        store.Rename(chat.Id, "  Report  ");
        chat.Entries.Add(new ChatEntry("e3", "user", "something else"));
        store.Save(chat);

        Assert.Equal("Report", store.List()[0].Title);
    }

    [Fact]
    public void The_newest_chats_come_first_and_empty_chats_are_not_kept()
    {
        var store = Store();
        var older = Chat("older");
        store.Save(older);
        _time.Advance(TimeSpan.FromMinutes(5));
        var newer = Chat("newer");
        store.Save(newer);
        store.Save(ChatRecord.New("copilot", _time.GetUtcNow()));

        Assert.Equal(["newer", "older"], store.List().Select(c => c.Title));
    }

    [Fact]
    public void An_older_copy_never_overwrites_a_newer_one()
    {
        // Saves happen from several threads (answers stream in while the person types): the last revision wins.
        var store = Store();
        var chat = Chat("hello");
        var stale = chat.Snapshot();
        chat.Entries.Add(new ChatEntry("e3", "user", "newer"));
        store.Save(chat);

        store.Save(stale);

        Assert.Equal(3, store.Load(chat.Id)!.Entries.Count);
    }

    [Fact]
    public void A_deleted_chat_can_be_brought_back_until_it_is_old()
    {
        var store = Store();
        var chat = Chat("oops");
        store.Save(chat);

        Assert.True(store.Delete(chat.Id));
        Assert.Empty(store.List());
        Assert.Null(store.Load(chat.Id));

        Assert.True(store.Restore(chat.Id));
        Assert.Equal("oops", Assert.Single(store.List()).Title);

        store.Delete(chat.Id);
        _time.Advance(TimeSpan.FromDays(31));
        Assert.False(Store().Restore(chat.Id));
    }

    [Fact]
    public void Search_finds_words_in_titles_and_messages_with_a_snippet()
    {
        var store = Store();
        var dentist = Chat("Book appointments");
        dentist.Entries.Add(new ChatEntry("e3", "agent", "I added a task to call the Dentist on Monday morning."));
        store.Save(dentist);
        store.Save(Chat("Groceries"));

        var found = Assert.Single(store.Search("dentist"));

        Assert.Equal(dentist.Id, found.Id);
        Assert.Contains("Dentist on Monday", found.Snippet, StringComparison.Ordinal);
        Assert.Equal("Groceries", Assert.Single(store.Search("GROCER")).Title);
        Assert.Empty(store.Search("   "));
    }

    [Theory]
    [InlineData("..\\evil")]
    [InlineData("../evil")]
    [InlineData("")]
    [InlineData("ABC")]
    public void Ids_that_are_not_ours_are_refused(string id)
    {
        var store = Store();

        Assert.Null(store.Load(id));
        Assert.False(store.Delete(id));
        Assert.False(store.Restore(id));
        Assert.Throws<KeyNotFoundException>(() => store.Rename(id, "x"));
    }

    [Fact]
    public void A_damaged_file_is_skipped_not_fatal()
    {
        Store().Save(Chat("fine"));
        File.WriteAllText(Path.Combine(_dir, "0123456789ab.json"), "{ not json");

        Assert.Equal("fine", Assert.Single(Store().List()).Title);
    }
}
