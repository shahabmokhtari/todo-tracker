using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

public sealed class StoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tt-tests-" + Guid.NewGuid().ToString("N"));

    private string BoardPath => Path.Combine(_dir, "board.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static TaskBoard SampleBoard()
    {
        var board = new TaskBoard();
        var x = board.AddTask(new NewTask("Feature X") { Priority = Priority.High, Details = "details", Deadline = T0.AddDays(7) }, Actor.User, T0);
        var a = board.AddTask(new NewTask("Feature A") { ParentId = x.Id, Sequential = true, StepDelay = TimeSpan.FromHours(24) }, Actor.User, T0);
        var a1 = board.AddTask(new NewTask("A1") { ParentId = a.Id }, Actor.User, T0);
        board.AddTask(new NewTask("A2") { ParentId = a.Id }, Actor.User, T0);
        board.AddNote(a1.Id, "note", Actor.Agent("copilot"), T0, "https://example.test", "Example");
        board.AddReminder(x.Id, T0.AddHours(2), "Check", Actor.User, T0);
        board.Complete(a1.Id, Actor.User, T0.AddHours(1));
        board.StartFocus(x.Id, Actor.User, T0);
        board.Pomodoro.Pause(T0.AddMinutes(10));
        return board;
    }

    [Fact]
    public void Serializer_round_trips_the_whole_board()
    {
        var original = SampleBoard();

        var json = BoardSerializer.Serialize(original);
        var restored = BoardSerializer.Deserialize(json);

        Assert.Equal(json, BoardSerializer.Serialize(restored));
        var a = restored.Items[0].Children[0];
        Assert.Same(restored.Items[0], a.Parent);
        Assert.True(a.Sequential);
        Assert.Equal(TimeSpan.FromHours(24), a.StepDelay);
        Assert.Equal(Actor.Agent("copilot"), a.Children[0].Notes[0].Author);
        Assert.Equal(T0.AddHours(25), a.Children[1].NextActionAt);
        Assert.Equal(TimeSpan.FromMinutes(15), restored.Pomodoro.Remaining(T0.AddDays(1)));
        Assert.Equal(original.Activity.Count, restored.Activity.Count);
    }

    [Fact]
    public void Serializer_uses_camel_case_utc_schema()
    {
        var json = BoardSerializer.Serialize(SampleBoard());

        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"priority\": \"high\"", json, StringComparison.Ordinal);
        Assert.Contains("\"stepDelayMinutes\": 1440", json, StringComparison.Ordinal);
        Assert.Contains("\"createdAt\": \"2026-01-05T09:00:00.000Z\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"agent\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serializer_rejects_newer_schema_versions()
    {
        Assert.Throws<NotSupportedException>(() => BoardSerializer.Deserialize("{\"schemaVersion\": 99, \"items\": []}"));
    }

    [Fact]
    public void Serializer_tolerates_missing_collections_and_offsets()
    {
        var board = BoardSerializer.Deserialize("""
            {"schemaVersion":1,"items":[{"id":"6f1f3c8e-0000-4000-8000-000000000001","title":"T","priority":"low","createdAt":"2026-01-05T01:00:00-08:00"}]}
            """);

        var item = Assert.Single(board.Items);
        Assert.Empty(item.Children);
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero), item.CreatedAt);
        Assert.Equal(PomodoroPhase.Idle, board.Pomodoro.Phase);
    }

    [Fact]
    public async Task File_store_starts_empty_and_persists_updates()
    {
        using (var store = FileBoardStore.Open(BoardPath))
        {
            Assert.Equal(0, await store.ReadAsync(b => b.Items.Count));
            await store.UpdateAsync(b => b.AddTask(new NewTask("Persist me"), Actor.User, T0));
        }

        using var reopened = FileBoardStore.Open(BoardPath);
        Assert.Equal("Persist me", await reopened.ReadAsync(b => b.Items[0].Title));
    }

    [Fact]
    public void File_store_allows_only_one_owner()
    {
        using var first = FileBoardStore.Open(BoardPath);

        Assert.Throws<StoreLockedException>(() => FileBoardStore.Open(BoardPath));
    }

    [Fact]
    public void File_store_can_be_reopened_after_dispose()
    {
        FileBoardStore.Open(BoardPath).Dispose();
        using var second = FileBoardStore.Open(BoardPath);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task Failed_update_rolls_back_in_memory_and_on_disk()
    {
        using var store = FileBoardStore.Open(BoardPath);
        var id = await store.UpdateAsync(b => b.AddTask(new NewTask("Keep"), Actor.User, T0).Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(b =>
        {
            b.AddTask(new NewTask("Discard"), Actor.User, T0);
            throw new InvalidOperationException("boom");
        }));

        Assert.Equal(["Keep"], await store.ReadAsync(b => b.Items.Select(i => i.Title).ToList()));
        Assert.DoesNotContain("Discard", await File.ReadAllTextAsync(BoardPath), StringComparison.Ordinal);
        Assert.NotNull(await store.ReadAsync(b => b.Find(id)));
    }

    [Fact]
    public async Task Update_raises_changed_event()
    {
        using var store = new InMemoryBoardStore();
        var raised = 0;
        store.Changed += (_, _) => raised++;

        await store.UpdateAsync(b => b.AddTask(new NewTask("x"), Actor.User, T0));

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Corrupt_board_falls_back_to_backup_and_keeps_the_corrupt_copy()
    {
        using (var store = FileBoardStore.Open(BoardPath))
        {
            await store.UpdateAsync(b => b.AddTask(new NewTask("first"), Actor.User, T0));
            await store.UpdateAsync(b => b.AddTask(new NewTask("second"), Actor.User, T0));
        }

        await File.WriteAllTextAsync(BoardPath, "{ not json");

        using var recovered = FileBoardStore.Open(BoardPath);
        Assert.Equal(["first"], await recovered.ReadAsync(b => b.Items.Select(i => i.Title).ToList()));
        Assert.Single(Directory.GetFiles(_dir, "board.json.corrupt-*"));
    }

    [Fact]
    public async Task Corrupt_board_without_backup_refuses_to_start()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(BoardPath, "{ not json");

        Assert.Throws<InvalidDataException>(() => FileBoardStore.Open(BoardPath));
    }
}
