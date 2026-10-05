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
        Assert.False(Directory.Exists(Path.Combine(_root, ".todo-tracker", "history.git")));
        Assert.True(Directory.Exists(_store.HistoryPath));
        Assert.StartsWith(_locks, _store.HistoryPath, StringComparison.OrdinalIgnoreCase);
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
    public async Task Version_messages_describe_only_new_changes_even_across_processes()
    {
        // The app, tt and tt mcp each keep their own VaultHistory: what was already described must be remembered on disk.
        var task = await _store.UpdateAsync(b => b.AddTask(new NewTask("Draft"), Actor.User, T0));
        Assert.True(await _history.CommitAsync());

        await _store.UpdateAsync(b =>
        {
            b.Update(task.Id, new TaskChanges { Title = "Final" }, Actor.Agent("claude"), T0.AddMinutes(1));
            return true;
        });
        using var other = VaultHistory.TryCreate(_store)!;
        Assert.True(await other.CommitAsync());

        var latest = (await other.LogAsync(null))[0].Message;
        Assert.DoesNotContain("Created", latest, StringComparison.Ordinal);
        Assert.Contains("claude", latest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restoring_keeps_edits_that_were_not_saved_as_a_version_yet()
    {
        var task = await _store.UpdateAsync(b => b.AddTask(new NewTask("Plan"), Actor.User, T0));
        await _history.CommitAsync();
        var first = (await _history.TaskHistoryAsync(task.Id))[0].Id;
        await _store.UpdateAsync(b =>
        {
            b.Update(task.Id, new TaskChanges { Details = "typed just now" }, Actor.User, T0);
            return true;
        });

        await _history.RestoreTaskAsync(task.Id, first, Actor.User);

        var versions = await _history.TaskHistoryAsync(task.Id);
        var texts = await Task.WhenAll(versions.Select(v => _history.TaskVersionAsync(task.Id, v.Id)));
        Assert.Contains(texts, t => t?.Contains("typed just now", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_lock_left_by_a_killed_git_does_not_stop_history()
    {
        await _store.UpdateAsync(b => b.AddTask(new NewTask("One"), Actor.User, T0));
        await _history.CommitAsync();
        var stale = Path.Combine(_store.HistoryPath, "index.lock");
        await File.WriteAllTextAsync(stale, string.Empty);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));

        await _store.UpdateAsync(b => b.AddTask(new NewTask("Two"), Actor.User, T0));

        Assert.True(await _history.CommitAsync());
    }

    [Fact]
    public async Task Attachments_are_not_versioned()
    {
        var task = await _store.UpdateAsync(b => b.AddTask(new NewTask("Logs"), Actor.User, T0));
        using (var content = new MemoryStream(new byte[1024]))
        {
            await _store.AddAttachmentAsync(task.Id, "dump.bin", content, Actor.User);
        }

        await _history.CommitAsync();

        var start = new System.Diagnostics.ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in new[] { $"--git-dir={_store.HistoryPath}", "ls-tree", "-r", "--name-only", "HEAD" })
        {
            start.ArgumentList.Add(arg);
        }

        using var git = System.Diagnostics.Process.Start(start)!;
        var tracked = (await git.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await git.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Work/Logs.md", tracked);
        Assert.DoesNotContain(tracked, t => t.StartsWith("_attachments/", StringComparison.Ordinal));
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
