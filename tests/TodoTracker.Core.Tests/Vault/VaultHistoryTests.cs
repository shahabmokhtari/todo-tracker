using Microsoft.Extensions.Time.Testing;
using TodoTracker.Core;
using TodoTracker.Core.Vault;

namespace TodoTracker.Core.Tests.Vault;

public sealed class VaultHistoryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tt-history-" + Guid.NewGuid().ToString("N"));
    private readonly string _locks = Path.Combine(Path.GetTempPath(), "tt-hlocks-" + Guid.NewGuid().ToString("N"));
    private readonly VaultBoardStore _store;
    private readonly VaultHistory _history;

    public VaultHistoryTests()
    {
        _store = VaultBoardStore.Open(new VaultOptions(_root) { TimeZone = TimeZoneInfo.Utc, Time = new FakeTimeProvider(T0), LockDirectory = _locks, Watch = false, EditSettleTime = TimeSpan.Zero });
        _history = VaultHistory.TryCreate(_store) ?? throw new InvalidOperationException("git is required for these tests");
    }

    public void Dispose()
    {
        _history.Dispose();
        _store.Dispose();
        foreach (var dir in new[] { _root, _locks })
        {
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Each_change_can_be_snapshotted_with_a_readable_message()
    {
        var task = await _store.UpdateAsync(b => b.AddTask(new NewTask("Ship"), Actor.User, T0));

        Assert.True(await _history.CommitAsync());
        Assert.False(await _history.CommitAsync());

        var versions = await _history.LogAsync(null);
        Assert.Contains("Ship", versions[0].Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(_root, ".todo-tracker", "history.git")));
        Assert.Equal(task.Id, await _store.ReadAsync(b => b.Items.Single().Id));
    }

    [Fact]
    public async Task A_task_has_its_own_history_and_old_versions_can_be_read()
    {
        var task = await _store.UpdateAsync(b => b.AddTask(new NewTask("Ship"), Actor.User, T0));
        await _history.CommitAsync();
        await _store.UpdateAsync(b => b.Update(task.Id, new TaskChanges { Title = "Ship it" }, Actor.User, T0));
        await _history.CommitAsync();
        await _store.UpdateAsync(b => b.AddTask(new NewTask("Unrelated"), Actor.User, T0));
        await _history.CommitAsync();

        var versions = await _history.TaskHistoryAsync(task.Id);

        Assert.Equal(2, versions.Count);
        Assert.Contains("# Ship it", await _history.TaskVersionAsync(task.Id, versions[0].Id), StringComparison.Ordinal);
        Assert.Contains("# Ship\n", (await _history.TaskVersionAsync(task.Id, versions[1].Id))!.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restoring_a_version_brings_the_task_back_and_is_itself_undoable()
    {
        var task = await _store.UpdateAsync(b => b.AddTask(new NewTask("Ship"), Actor.User, T0));
        await _store.UpdateAsync(b => b.AddTask(new NewTask("Step 1") { ParentId = task.Id }, Actor.User, T0));
        await _history.CommitAsync();
        var good = (await _history.TaskHistoryAsync(task.Id))[0].Id;
        await _store.UpdateAsync(b => b.Update(task.Id, new TaskChanges { Title = "Oops" }, Actor.User, T0));
        await _history.CommitAsync();

        await _history.RestoreTaskAsync(task.Id, good, Actor.User);

        Assert.Equal("Ship", await _store.ReadAsync(b => b.Get(task.Id).Title));
        Assert.Equal("Step 1", await _store.ReadAsync(b => b.Get(task.Id).Children.Single().Title));
        Assert.Contains(await _store.ReadAsync(b => b.Activity.ToList()), a => a.ItemId == task.Id && a.Summary.Contains("Restored", StringComparison.Ordinal));
        Assert.Equal(3, (await _history.TaskHistoryAsync(task.Id)).Count);
    }

    [Fact]
    public async Task Edits_made_outside_the_app_are_captured_too()
    {
        await _store.UpdateAsync(b => b.AddTask(new NewTask("Ship"), Actor.User, T0));
        await _history.CommitAsync();
        var file = Path.Combine(_root, "Work", "Ship.md");
        await File.AppendAllTextAsync(file, "\nEdited in Obsidian.\n");

        Assert.True(await _history.CommitAsync());
        Assert.Contains("Edited outside the app", (await _history.LogAsync(null))[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_versions_are_rejected_safely()
    {
        var task = await _store.UpdateAsync(b => b.AddTask(new NewTask("Ship"), Actor.User, T0));
        await _history.CommitAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => _history.TaskVersionAsync(task.Id, "--output=/tmp/x"));
        Assert.Null(await _history.TaskVersionAsync(task.Id, new string('a', 40)));
    }
}
