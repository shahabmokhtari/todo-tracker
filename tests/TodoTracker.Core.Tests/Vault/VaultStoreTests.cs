using Microsoft.Extensions.Time.Testing;
using TodoTracker.Core;
using TodoTracker.Core.Vault;

namespace TodoTracker.Core.Tests.Vault;

public sealed class VaultStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tt-vault-" + Guid.NewGuid().ToString("N"));
    private readonly string _locks = Path.Combine(Path.GetTempPath(), "tt-locks-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(T0);
    private readonly List<VaultBoardStore> _stores = [];

    public void Dispose()
    {
        foreach (var store in _stores)
        {
            store.Dispose();
        }

        foreach (var dir in new[] { _root, _locks })
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private VaultBoardStore Open(string? legacy = null)
    {
        var store = VaultBoardStore.Open(new VaultOptions(_root) { TimeZone = TimeZoneInfo.Utc, Time = _time, LockDirectory = _locks, LegacyBoardPath = legacy, Watch = false });
        _stores.Add(store);
        return store;
    }

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private Task<WorkItem> Add(VaultBoardStore store, string title, Guid? parent = null, Guid? group = null) =>
        store.UpdateAsync(b => b.AddTask(new NewTask(title) { ParentId = parent, GroupId = group }, Actor.User, _time.GetUtcNow()));

    [Fact]
    public async Task A_new_vault_has_group_folders_a_config_and_a_guide_for_agents()
    {
        var store = Open();

        Assert.True(Directory.Exists(P("Work")));
        Assert.True(Directory.Exists(P("Personal")));
        Assert.True(File.Exists(P(".todo-tracker", "config.json")));
        Assert.Contains("Todo Tracker vault", await File.ReadAllTextAsync(P("AGENTS.md")), StringComparison.Ordinal);
        Assert.Equal(["Work", "Personal"], await store.ReadAsync(b => b.Groups.Select(g => g.Name).ToList()));
    }

    [Fact]
    public async Task Each_top_level_task_is_one_markdown_file_in_its_group_folder()
    {
        var store = Open();

        var task = await Add(store, "Ship release 2.3");
        await Add(store, "Roll out A", task.Id);

        var file = P("Work", "Ship release 2.3.md");
        var text = await File.ReadAllTextAsync(file);
        Assert.Contains("# Ship release 2.3", text, StringComparison.Ordinal);
        Assert.Contains("- [ ] Roll out A", text, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(P("Work"), "*.md"));
        Assert.Equal("Work/Ship release 2.3.md", store.PathOf(task.Id));
    }

    [Fact]
    public async Task Tasks_survive_reopening_the_vault()
    {
        var store = Open();
        var task = await Add(store, "Ship");
        await store.UpdateAsync(b => b.SetTags(task.Id, ["release"], Actor.User, T0));
        await store.UpdateAsync(b => b.SetLabels(task.Id, ["Deep work"], Actor.User, T0));
        var color = await store.ReadAsync(b => b.Labels[0].Color);
        await store.UpdateAsync(b => b.StartFocus(task.Id, Actor.User, T0));
        store.Dispose();

        var reopened = Open();

        var (title, tags, labelColor, focus, activity) = await reopened.ReadAsync(b => (b.Get(task.Id).Title, b.Get(task.Id).Tags.ToList(), b.Labels[0].Color, b.Pomodoro.ItemId, b.Activity.Count));
        Assert.Equal(("Ship", color, task.Id), (title, labelColor, focus));
        Assert.Equal(["release"], tags);
        Assert.True(activity >= 4);
    }

    [Fact]
    public async Task Same_titles_get_distinct_files_and_renames_keep_the_file()
    {
        var store = Open();
        var a = await Add(store, "Call: vendor?");
        var b = await Add(store, "Call: vendor?");

        await store.UpdateAsync(x => x.Update(a.Id, new TaskChanges { Title = "Call the vendor" }, Actor.User, T0));

        Assert.Equal("Work/Call vendor.md", store.PathOf(a.Id));
        Assert.Equal("Work/Call vendor 2.md", store.PathOf(b.Id));
        Assert.Contains("# Call the vendor", await File.ReadAllTextAsync(P("Work", "Call vendor.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_group_moves_the_file_and_deleting_moves_it_to_trash()
    {
        var store = Open();
        var task = await Add(store, "Dentist");
        var personal = await store.ReadAsync(b => b.Groups[1].Id);

        await store.UpdateAsync(b => b.MoveToGroup(task.Id, personal, Actor.User, T0));
        Assert.True(File.Exists(P("Personal", "Dentist.md")));
        Assert.False(File.Exists(P("Work", "Dentist.md")));

        await store.UpdateAsync(b => b.Delete(task.Id, Actor.User, T0));
        Assert.False(File.Exists(P("Personal", "Dentist.md")));
        Assert.Single(Directory.GetFiles(P(".todo-tracker", "trash"), "*Dentist.md"));
    }

    [Fact]
    public async Task A_subtask_promoted_to_the_top_level_gets_its_own_file()
    {
        var store = Open();
        var project = await Add(store, "Project");
        var step = await Add(store, "Spin off", project.Id);

        await store.UpdateAsync(b => b.Move(step.Id, null, null, null, Actor.User, T0));

        Assert.True(File.Exists(P("Work", "Spin off.md")));
        Assert.DoesNotContain("Spin off", await File.ReadAllTextAsync(P("Work", "Project.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checking_a_step_in_obsidian_completes_it_unlocks_the_next_and_logs_the_edit()
    {
        var store = Open();
        var rollout = await store.UpdateAsync(b => b.AddTask(new NewTask("Rollout") { Sequential = true, StepDelay = TimeSpan.FromHours(24) }, Actor.User, T0));
        var (first, second) = await store.UpdateAsync(b =>
        {
            var steps = b.AddSteps(rollout.Id, ["Ring 0", "Ring 1"], TimeSpan.FromHours(24), Actor.User, T0);
            return (steps[0].Id, steps[1].Id);
        });

        var file = P("Work", "Rollout.md");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("1. [ ] Ring 0", "1. [x] Ring 0", StringComparison.Ordinal));
        _time.Advance(TimeSpan.FromMinutes(5));
        store.MarkDirty();

        var (done, gate, entry) = await store.ReadAsync(b => (b.Get(first).IsDone, b.Get(second).NextActionAt, b.Activity.Last(a => a.ItemId == first)));
        Assert.True(done);
        Assert.Equal(_time.GetUtcNow().AddHours(24), gate);
        Assert.Equal((ActivityKind.Completed, ActorKind.Vault), (entry.Kind, entry.Actor.Kind));
        Assert.Contains("✅", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_note_created_in_a_group_folder_becomes_a_task()
    {
        var store = Open();
        await File.WriteAllTextAsync(P("Personal", "Groceries.md"), "- [ ] Milk\n- [ ] Bread\n");
        store.MarkDirty();

        var (title, steps, group) = await store.ReadAsync(b =>
        {
            var t = b.Items.Single();
            return (t.Title, t.Children.Count, b.GetGroup(t.GroupId).Name);
        });

        Assert.Equal(("Groceries", 2, "Personal"), (title, steps, group));
        Assert.StartsWith("---\nid: ", (await File.ReadAllTextAsync(P("Personal", "Groceries.md"))).ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task New_folders_become_groups_and_renamed_folders_keep_their_identity()
    {
        var store = Open();
        Directory.CreateDirectory(P("Side projects"));
        store.MarkDirty();
        var sideId = await store.ReadAsync(b => b.Groups.Single(g => g.Name == "Side projects").Id);
        await store.UpdateAsync(b => b.UpdateGroup(sideId, "Side projects", "#ff0000", Actor.User, T0));
        await Add(store, "Build a robot", group: sideId);

        Directory.Move(P("Side projects"), P("Hobbies"));
        store.MarkDirty();

        var hobbies = await store.ReadAsync(b => b.Groups.Single(g => g.Name == "Hobbies"));
        Assert.Equal((sideId, "#ff0000"), (hobbies.Id, hobbies.Color));
        Assert.DoesNotContain(await store.ReadAsync(b => b.Groups.Select(g => g.Name).ToList()), n => n == "Side projects");
    }

    [Fact]
    public async Task Renaming_a_group_renames_its_folder()
    {
        var store = Open();
        var work = await store.ReadAsync(b => b.Groups[0].Id);
        await Add(store, "Ship");

        await store.UpdateAsync(b => b.UpdateGroup(work, "Office", null, Actor.User, T0));

        Assert.True(File.Exists(P("Office", "Ship.md")));
        Assert.False(Directory.Exists(P("Work")));
    }

    [Fact]
    public async Task A_broken_file_is_reported_and_never_overwritten()
    {
        var store = Open();
        var task = await Add(store, "Ship");
        var file = P("Work", "Ship.md");
        const string broken = "---\nid: [oops\n---\n# Ship\n";
        await File.WriteAllTextAsync(file, broken);
        store.MarkDirty();

        // The rest of the board keeps working, the last good version stays visible, and the file is left alone.
        await Add(store, "Other");
        var problem = Assert.Single(store.Problems);
        Assert.Equal("Work/Ship.md", problem.Path);
        Assert.True(await store.ReadAsync(b => b.Find(task.Id) is not null));
        Assert.Equal(broken, await File.ReadAllTextAsync(file));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(b => b.Update(task.Id, new TaskChanges { Title = "x" }, Actor.User, T0)));

        await File.WriteAllTextAsync(file, "# Ship\n");
        store.MarkDirty();
        Assert.Empty(await store.ReadAsync(_ => store.Problems));
    }

    [Fact]
    public async Task A_copied_task_file_gets_its_own_identity()
    {
        var store = Open();
        var task = await Add(store, "Ship");
        File.Copy(P("Work", "Ship.md"), P("Work", "Ship copy.md"));
        store.MarkDirty();

        var ids = await store.ReadAsync(b => b.Items.Select(i => i.Id).ToList());

        Assert.Equal(2, ids.Distinct().Count());
        Assert.Contains(task.Id, ids);
        Assert.Equal(task.Id, await store.ReadAsync(b => b.Items.Single(i => store.PathOf(i.Id) == "Work/Ship.md").Id));
    }

    [Fact]
    public async Task Files_nobody_changed_are_never_rewritten()
    {
        var store = Open();
        await Add(store, "Untouched");
        var file = P("Work", "Untouched.md");
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, stamp);

        await Add(store, "Another");

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public async Task Two_processes_sharing_a_vault_never_lose_each_others_changes()
    {
        var a = Open();
        var b = Open();

        var fromA = await Add(a, "From A");
        var fromB = await Add(b, "From B");
        await a.UpdateAsync(x => x.AddNote(fromB.Id, "seen by A", Actor.User, T0));

        var titles = await b.ReadAsync(x => x.Items.Select(i => i.Title).OrderBy(t => t).ToList());
        Assert.Equal(["From A", "From B"], titles);
        Assert.Equal("seen by A", await b.ReadAsync(x => x.Get(fromB.Id).Notes.Single().Text));
        Assert.Equal(fromA.Id, await a.ReadAsync(x => x.Items[0].Id));
    }

    [Fact]
    public async Task An_edit_racing_a_save_is_merged_instead_of_overwritten()
    {
        var store = Open();
        var task = await Add(store, "Ship");
        var file = P("Work", "Ship.md");
        var attempts = 0;

        await store.UpdateAsync(b =>
        {
            if (attempts++ == 0)
            {
                // Obsidian saves the file while the app is in the middle of its own change.
                File.WriteAllText(file, File.ReadAllText(file).Replace("# Ship", "# Ship it", StringComparison.Ordinal) + "\nAdded in Obsidian.\n");
            }

            b.AddNote(task.Id, "from the app", Actor.User, T0);
        });

        var text = await File.ReadAllTextAsync(file);
        Assert.Contains("# Ship it", text, StringComparison.Ordinal);
        Assert.Contains("Added in Obsidian.", text, StringComparison.Ordinal);
        Assert.Contains("from the app", text, StringComparison.Ordinal);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task A_failed_change_leaves_everything_as_it_was()
    {
        var store = Open();
        var task = await Add(store, "Ship");
        var before = await File.ReadAllTextAsync(P("Work", "Ship.md"));

        await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateAsync(b =>
        {
            b.AddNote(task.Id, "half done", Actor.User, T0);
            b.SetTags(task.Id, ["not valid"], Actor.User, T0);
        }));

        Assert.Empty(await store.ReadAsync(b => b.Get(task.Id).Notes.ToList()));
        Assert.Equal(before, await File.ReadAllTextAsync(P("Work", "Ship.md")));
    }

    [Fact]
    public async Task An_existing_board_json_is_migrated_once()
    {
        var legacyBoard = new TaskBoard();
        var old = legacyBoard.AddTask(new NewTask("Legacy task"), Actor.User, T0);
        legacyBoard.AddTask(new NewTask("Legacy step") { ParentId = old.Id }, Actor.User, T0);
        var legacy = Path.Combine(_locks, "board.json");
        Directory.CreateDirectory(_locks);
        await File.WriteAllTextAsync(legacy, BoardSerializer.Serialize(legacyBoard));

        var store = Open(legacy);

        Assert.Equal("Legacy step", await store.ReadAsync(b => b.Get(old.Id).Children.Single().Title));
        Assert.True(File.Exists(P("Work", "Legacy task.md")));
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(legacy + ".migrated"));
        Assert.True(await store.ReadAsync(b => b.Activity.Count) >= 2);
    }

    [Fact]
    public async Task Attachments_are_stored_next_to_the_vault_and_can_be_read_back()
    {
        var store = Open();
        var task = await Add(store, "Ship");
        var step = await Add(store, "Step", task.Id);

        var attachment = await store.AddAttachmentAsync(step.Id, "plan.txt", new MemoryStream("hello"u8.ToArray()), Actor.User);
        var again = await store.AddAttachmentAsync(step.Id, "plan.txt", new MemoryStream("second"u8.ToArray()), Actor.User);

        Assert.Equal(5, attachment.Size);
        Assert.NotEqual(attachment.Path, again.Path);
        var (full, name) = await store.GetAttachmentFileAsync(step.Id, attachment.Id);
        Assert.Equal(("plan.txt", "hello"), (name, await File.ReadAllTextAsync(full)));
        Assert.Contains("📎 [plan.txt]", await File.ReadAllTextAsync(P("Work", "Ship.md")), StringComparison.Ordinal);

        await store.RemoveAttachmentAsync(step.Id, attachment.Id, Actor.User);
        Assert.False(File.Exists(full));
    }

    [Fact]
    public async Task Attachment_links_cannot_escape_the_vault()
    {
        var store = Open();
        var task = await Add(store, "Ship");
        var file = P("Work", "Ship.md");
        await File.AppendAllTextAsync(file, "\n## Attachments\n\n- [secret](../../../../etc/passwd)\n");
        store.MarkDirty();
        var attachmentId = await store.ReadAsync(b => b.Get(task.Id).Attachments.Single().Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.GetAttachmentFileAsync(task.Id, attachmentId));
    }

    [Fact]
    public async Task Oversized_attachments_are_rejected()
    {
        var store = VaultBoardStore.Open(new VaultOptions(_root) { TimeZone = TimeZoneInfo.Utc, Time = _time, LockDirectory = _locks, Watch = false, MaxAttachmentBytes = 4 });
        _stores.Add(store);
        var task = await Add(store, "Ship");

        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAttachmentAsync(task.Id, "big.bin", new MemoryStream(new byte[5]), Actor.User));
        Assert.Empty(await store.ReadAsync(b => b.Get(task.Id).Attachments.ToList()));
    }

    [Fact]
    public async Task A_rich_html_version_lives_next_to_the_markdown_and_moves_with_it()
    {
        var store = Open();
        var task = await Add(store, "Ship");

        await store.WriteRichAsync(task.Id, "<h1>Plan</h1>", Actor.Agent("copilot"));
        Assert.Equal("<h1>Plan</h1>", await store.ReadRichAsync(task.Id));
        Assert.True(File.Exists(P("Work", "Ship.html")));

        var personal = await store.ReadAsync(b => b.Groups[1].Id);
        await store.UpdateAsync(b => b.MoveToGroup(task.Id, personal, Actor.User, T0));
        Assert.True(File.Exists(P("Personal", "Ship.html")));

        await store.WriteRichAsync(task.Id, null, Actor.User);
        Assert.Null(await store.ReadRichAsync(task.Id));
        Assert.False(File.Exists(P("Personal", "Ship.html")));
    }

    [Fact]
    public async Task Rich_content_belongs_to_top_level_tasks()
    {
        var store = Open();
        var task = await Add(store, "Ship");
        var step = await Add(store, "Step", task.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteRichAsync(step.Id, "<p>x</p>", Actor.User));
    }

    [Fact]
    public async Task External_edits_raise_changed_when_watching()
    {
        var store = VaultBoardStore.Open(new VaultOptions(_root) { TimeZone = TimeZoneInfo.Utc, Time = _time, LockDirectory = _locks, Watch = true });
        _stores.Add(store);
        var changed = new TaskCompletionSource();
        store.Changed += (_, _) => changed.TrySetResult();

        await File.WriteAllTextAsync(P("Work", "From obsidian.md"), "# From obsidian\n");

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("From obsidian", await store.ReadAsync(b => b.Items.Single().Title));
    }
}
