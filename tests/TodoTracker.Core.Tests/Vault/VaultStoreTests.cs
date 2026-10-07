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
        var store = VaultBoardStore.Open(new VaultOptions(_root) { TimeZone = TimeZoneInfo.Utc, Time = _time, LockDirectory = _locks, LegacyBoardPath = legacy, Watch = false, EditSettleTime = TimeSpan.Zero });
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
    public async Task Board_column_time_and_archive_are_kept_in_the_file_and_read_back()
    {
        var store = Open();
        var task = await Add(store, "Ship release 2.3");
        var step = await Add(store, "Roll out A", task.Id);
        await store.UpdateAsync(b => b.StartTimer(step.Id, Actor.User, T0, "laptop"));
        _time.Advance(TimeSpan.FromMinutes(25));
        await store.UpdateAsync(b => b.StopTimer(_time.GetUtcNow()));
        await store.UpdateAsync(b => b.Complete(task.Id, Actor.User, _time.GetUtcNow()));
        await store.UpdateAsync(b => b.Archive(task.Id, Actor.User, _time.GetUtcNow()));

        var text = await File.ReadAllTextAsync(P("Work", "Ship release 2.3.md"));
        Assert.Contains("stage: doing", text, StringComparison.Ordinal);
        Assert.Contains("archived:", text, StringComparison.Ordinal);
        Assert.Contains("## Time\n\n- 2026-01-05 09:00–09:25 · 25 min · [[#^", text.ReplaceLineEndings("\n"), StringComparison.Ordinal);

        // Archiving keeps the file where it is (links to it keep working).
        Assert.Equal("Work/Ship release 2.3.md", store.PathOf(task.Id));

        store.Dispose();
        var reopened = Open();
        var (stage, archived, spent) = await reopened.ReadAsync(b => (b.Get(task.Id).Stage, b.Get(task.Id).ArchivedAt, b.Get(task.Id).TimeSpent(_time.GetUtcNow())));
        Assert.Equal(Stage.Doing, stage);
        Assert.NotNull(archived);
        Assert.Equal(TimeSpan.FromMinutes(25), spent);
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
        Assert.Equal("- [ ] Milk\n- [ ] Bread\n", await File.ReadAllTextAsync(P("Personal", "Groceries.md")));
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
    public async Task Images_pasted_in_obsidian_resolve_from_the_enclosing_obsidian_vault_but_nothing_outside_it()
    {
        // Tasks live in <obsidian vault>/Todo Tracker; Obsidian saves pasted images at the vault root by default.
        Directory.CreateDirectory(P(".obsidian"));
        await File.WriteAllBytesAsync(P("Pasted image 1.png"), [1, 2, 3]);
        var store = VaultBoardStore.Open(new VaultOptions(P("Todo Tracker")) { TimeZone = TimeZoneInfo.Utc, Time = _time, LockDirectory = _locks, Watch = false, EditSettleTime = TimeSpan.Zero });
        _stores.Add(store);
        var task = await Add(store, "Ship");
        var file = P("Todo Tracker", "Work", "Ship.md");
        await File.AppendAllTextAsync(file, "\n## Attachments\n\n- ![[Pasted image 1.png]]\n- [secret](../../../../etc/passwd)\n");
        store.MarkDirty();
        var attachments = await store.ReadAsync(b => b.Get(task.Id).Attachments.ToList());

        var (path, _) = await store.GetAttachmentFileAsync(task.Id, attachments[0].Id);
        Assert.Equal(P("Pasted image 1.png"), path);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.GetAttachmentFileAsync(task.Id, attachments[1].Id));
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
        var store = VaultBoardStore.Open(new VaultOptions(_root) { TimeZone = TimeZoneInfo.Utc, Time = _time, LockDirectory = _locks, Watch = true, EditSettleTime = TimeSpan.Zero });
        _stores.Add(store);
        var changed = new TaskCompletionSource();
        store.Changed += (_, _) => changed.TrySetResult();

        await File.WriteAllTextAsync(P("Work", "From obsidian.md"), "# From obsidian\n");

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("From obsidian", await store.ReadAsync(b => b.Items.Single().Title));
    }

    // ---- Adopting an existing folder ----------------------------------------------------

    [Fact]
    public async Task Opening_a_folder_of_existing_notes_never_rewrites_them()
    {
        Directory.CreateDirectory(P("Notes"));
        var note = P("Notes", "Ideas.md");
        const string original = "# Ideas\n\n- [ ] Write a book\n- [ ] Learn Rust\n";
        await File.WriteAllTextAsync(note, original);
        var stamp = File.GetLastWriteTimeUtc(note);

        var store = Open();
        var ids = await store.ReadAsync(b => b.Items.Single().SelfAndDescendants().Select(i => i.Id).ToList());
        await Add(store, "Something else");
        store.Dispose();
        var again = await Open().ReadAsync(b => b.Items.Single(i => i.Title == "Ideas").SelfAndDescendants().Select(i => i.Id).ToList());

        Assert.Equal(original, await File.ReadAllTextAsync(note));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(note));
        Assert.Equal(ids, again);
        Assert.False(File.Exists(P("AGENTS.md")), "an existing notes folder doesn't get a guide note added to it");
    }

    [Fact]
    public void A_folder_can_be_inspected_before_it_is_adopted()
    {
        Directory.CreateDirectory(P("Projects", "Old"));
        File.WriteAllText(P("Projects", "Plan.md"), "# Plan");
        File.WriteAllText(P("Projects", "Old", "Notes.md"), "x");
        Directory.CreateDirectory(P("Journal"));
        File.WriteAllText(P("Journal", "Today.md"), "x");
        Directory.CreateDirectory(P(".obsidian"));
        File.WriteAllText(P(".obsidian", "app.md"), "x");
        File.WriteAllText(P("Readme.md"), "x");

        var summary = VaultFolder.Inspect(_root);

        Assert.Equal(new VaultFolderSummary(Exists: true, IsTaskFolder: false, Groups: 2, Tasks: 3), summary);
        Assert.Equal(new VaultFolderSummary(false, false, 0, 0), VaultFolder.Inspect(P("missing")));
        Open().Dispose();
        Assert.True(VaultFolder.Inspect(_root).IsTaskFolder);
    }

    [Fact]
    public async Task Changing_a_hand_written_task_only_rewrites_that_file()
    {
        Directory.CreateDirectory(P("Notes"));
        await File.WriteAllTextAsync(P("Notes", "A.md"), "# A\n\n- [ ] one\n");
        await File.WriteAllTextAsync(P("Notes", "B.md"), "# B\n\n- [ ] two\n");
        var store = Open();
        var one = await store.ReadAsync(b => b.Items.Single(i => i.Title == "A").Children[0].Id);

        await store.UpdateAsync(b => b.Complete(one, Actor.User, T0));

        Assert.Contains("- [x] one", await File.ReadAllTextAsync(P("Notes", "A.md")), StringComparison.Ordinal);
        Assert.Equal("# B\n\n- [ ] two\n", await File.ReadAllTextAsync(P("Notes", "B.md")));
    }

    // ---- All-or-nothing saves -----------------------------------------------------------

    [Fact]
    public async Task A_save_that_fails_halfway_leaves_every_file_as_it_was()
    {
        var failOn = (string?)null;
        var store = VaultBoardStore.Open(new VaultOptions(_root) { TimeZone = TimeZoneInfo.Utc, Time = _time, LockDirectory = _locks, Watch = false, EditSettleTime = TimeSpan.Zero, BeforeWrite = p => { if (p == failOn) throw new IOException("disk full"); } });
        _stores.Add(store);
        var a = await Add(store, "A");
        var b = await Add(store, "B");
        var step = await Add(store, "Step", a.Id);
        var aBefore = await File.ReadAllTextAsync(P("Work", "A.md"));
        var bBefore = await File.ReadAllTextAsync(P("Work", "B.md"));

        // B (gaining the step) is written first; A fails, so B must be put back.
        failOn = "Work/A.md";
        await Assert.ThrowsAsync<IOException>(() => store.UpdateAsync(x => x.Move(step.Id, b.Id, null, null, Actor.User, T0)));

        Assert.Equal(aBefore, await File.ReadAllTextAsync(P("Work", "A.md")));
        Assert.Equal(bBefore, await File.ReadAllTextAsync(P("Work", "B.md")));
        Assert.Equal(a.Id, await store.ReadAsync(x => x.Get(step.Id).Parent!.Id));

        failOn = null;
        await store.UpdateAsync(x => x.Move(step.Id, b.Id, null, null, Actor.User, T0));
        Assert.Contains("Step", await File.ReadAllTextAsync(P("Work", "B.md")), StringComparison.Ordinal);
        Assert.DoesNotContain("Step", await File.ReadAllTextAsync(P("Work", "A.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edit_that_keeps_size_and_time_is_still_not_overwritten()
    {
        var store = Open();
        var task = await Add(store, "Ship");
        await Add(store, "Step", task.Id);
        var file = P("Work", "Ship.md");
        var stamp = File.GetLastWriteTimeUtc(file);
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("- [ ] Step", "- [x] Step", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(file, stamp);

        await store.UpdateAsync(b => b.AddNote(task.Id, "from the app", Actor.User, T0));

        var text = await File.ReadAllTextAsync(file);
        Assert.Contains("- [x] Step", text, StringComparison.Ordinal);
        Assert.Contains("from the app", text, StringComparison.Ordinal);
    }

    // ---- Edits made outside the app -----------------------------------------------------

    [Fact]
    public async Task An_agent_marking_a_step_done_in_frontmatter_style_is_attributed_once_across_processes()
    {
        var a = Open();
        var b = Open();
        var rollout = await a.UpdateAsync(x => x.AddTask(new NewTask("Rollout") { Sequential = true, StepDelay = TimeSpan.FromHours(1) }, Actor.User, T0));
        var (first, second) = await a.UpdateAsync(x =>
        {
            var steps = x.AddSteps(rollout.Id, ["Ring 0", "Ring 1"], TimeSpan.FromHours(1), Actor.User, T0);
            return (steps[0].Id, steps[1].Id);
        });
        await b.ReadAsync(_ => 0);

        // A tool that writes full metadata (so nothing needs normalizing) still triggers the sequence.
        var file = P("Work", "Rollout.md");
        var text = await File.ReadAllTextAsync(file);
        text = text.Replace("1. [ ] Ring 0", "1. [x] Ring 0", StringComparison.Ordinal)
            .Replace($"\"id\":\"{first}\",\"created\":\"2026-01-05T09:00:00.000Z\"", $"\"id\":\"{first}\",\"created\":\"2026-01-05T09:00:00.000Z\",\"done\":\"2026-01-05T09:00:00.000Z\"", StringComparison.Ordinal);
        await File.WriteAllTextAsync(file, text);

        await a.ReadAsync(_ => 0);
        await b.ReadAsync(_ => 0);
        await a.ReadAsync(_ => 0);

        Assert.Equal(T0.AddHours(1), await b.ReadAsync(x => x.Get(second).NextActionAt));
        Assert.Single(await b.ReadAsync(x => x.Activity.Where(e => e.ItemId == first && e.Kind == ActivityKind.Completed).ToList()));
    }

    [Fact]
    public async Task Edits_are_picked_up_but_only_acted_on_once_the_file_is_quiet()
    {
        var store = VaultBoardStore.Open(new VaultOptions(_root) { TimeZone = TimeZoneInfo.Utc, Time = _time, LockDirectory = _locks, Watch = false, EditSettleTime = TimeSpan.FromHours(1) });
        _stores.Add(store);
        var task = await Add(store, "Ship");
        var step = await Add(store, "Step", task.Id);
        var file = P("Work", "Ship.md");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("- [ ] Step", "- [x] Step", StringComparison.Ordinal));
        var written = await File.ReadAllTextAsync(file);

        Assert.True(await store.ReadAsync(b => b.Get(step.Id).IsDone));
        Assert.Equal(written, await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task An_edit_is_acted_on_once_it_settles_even_if_the_app_saved_something_else_meanwhile()
    {
        var store = VaultBoardStore.Open(new VaultOptions(_root) { TimeZone = TimeZoneInfo.Utc, Time = _time, LockDirectory = _locks, Watch = false, EditSettleTime = TimeSpan.FromSeconds(2) });
        _stores.Add(store);
        var rollout = await store.UpdateAsync(b => b.AddTask(new NewTask("Rollout") { Sequential = true }, Actor.User, T0));
        var (first, second) = await store.UpdateAsync(b =>
        {
            var steps = b.AddSteps(rollout.Id, ["Ring 0", "Ring 1"], TimeSpan.FromHours(24), Actor.User, T0);
            return (steps[0].Id, steps[1].Id);
        });
        var other = await Add(store, "Something else");
        var file = P("Work", "Rollout.md");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("1. [ ] Ring 0", "1. [x] Ring 0", StringComparison.Ordinal));

        Assert.True(await store.ReadAsync(b => b.Get(first).IsDone));
        Assert.Null(await store.ReadAsync(b => b.Get(second).NextActionAt));

        // The app saves an unrelated change while the edit is still settling: the edit must not be forgotten.
        await store.UpdateAsync(b => b.AddNote(other.Id, "busy", Actor.User, _time.GetUtcNow()));
        _time.Advance(TimeSpan.FromSeconds(3));

        var (gate, completions) = await store.ReadAsync(b => (b.Get(second).NextActionAt, b.Activity.Count(a => a.ItemId == first && a.Kind == ActivityKind.Completed)));
        Assert.Equal(_time.GetUtcNow().AddHours(24), gate);
        Assert.Equal(1, completions);
    }

    [Fact]
    public async Task Cascaded_completions_are_not_logged_again_by_another_process()
    {
        var a = Open();
        var b = Open();
        var parent = await Add(a, "Parent");
        await Add(a, "One", parent.Id);
        await Add(a, "Two", parent.Id);
        await b.ReadAsync(_ => 0);

        await a.UpdateAsync(x =>
        {
            x.Complete(parent.Id, Actor.User, _time.GetUtcNow());
            return true;
        });
        var logged = await a.ReadAsync(x => x.Activity.Count);
        await b.ReadAsync(_ => 0);
        await a.ReadAsync(_ => 0);

        Assert.Equal(logged, await b.ReadAsync(x => x.Activity.Count));
        Assert.Equal(logged, await a.ReadAsync(x => x.Activity.Count));
    }

    [Fact]
    public async Task A_completion_followed_by_a_note_is_not_replayed_by_another_process()
    {
        var a = Open();
        var b = Open();
        var rollout = await a.UpdateAsync(x => x.AddTask(new NewTask("Rollout") { Sequential = true }, Actor.User, T0));
        var (first, second) = await a.UpdateAsync(x =>
        {
            var steps = x.AddSteps(rollout.Id, ["Ring 0", "Ring 1"], TimeSpan.FromHours(1), Actor.User, T0);
            return (steps[0].Id, steps[1].Id);
        });
        await b.ReadAsync(_ => 0);

        await a.UpdateAsync(x =>
        {
            x.Complete(first, Actor.User, T0);
            return x.AddNote(first, "went fine", Actor.User, T0);
        });
        _time.Advance(TimeSpan.FromMinutes(30));
        await b.ReadAsync(_ => 0);

        Assert.Equal(T0.AddHours(1), await b.ReadAsync(x => x.Get(second).NextActionAt));
        Assert.Single(await b.ReadAsync(x => x.Activity.Where(e => e.ItemId == first && e.Kind == ActivityKind.Completed).ToList()));
    }

    [Fact]
    public async Task Checking_a_hand_written_step_with_the_obsidian_tasks_plugin_keeps_its_identity()
    {
        var store = Open();
        await File.WriteAllTextAsync(P("Work", "Trip.md"), "# Trip\n\n- [ ] Book flights\n- [ ] Pack\n");
        var flights = await store.ReadAsync(b => b.AllItems().Single(i => i.Title == "Book flights").Id);

        // What the Tasks plugin writes when the box is checked, plus a tag and priority added by hand.
        await File.WriteAllTextAsync(P("Work", "Trip.md"), "# Trip\n\n- [x] Book flights #travel ⏫ ✅ 2026-01-05\n- [ ] Pack\n");
        _time.Advance(TimeSpan.FromMinutes(1));

        var item = await store.ReadAsync(b => b.Find(flights));
        Assert.NotNull(item);
        Assert.True(item.IsDone);
        Assert.Single(await store.ReadAsync(b => b.Activity.Where(e => e.ItemId == flights && e.Kind == ActivityKind.Completed).ToList()));
    }

    [Fact]
    public async Task The_do_now_order_is_shared_and_survives_reopening()
    {
        var a = Open();
        var b = Open();
        var first = await Add(a, "First");
        var second = await Add(a, "Second");

        await a.UpdateAsync(x =>
        {
            x.ArrangeNow([second.Id, first.Id]);
            return true;
        });

        Assert.Equal([second.Id, first.Id], await b.ReadAsync(x => x.NowOrder.ToList()));
        a.Dispose();
        Assert.Equal("Second", await Open().ReadAsync(x => Agenda.Build(x, T0).Focus!.Item.Title));
    }

    // ---- Files and folders --------------------------------------------------------------

    [Fact]
    public async Task Moving_a_task_to_another_group_keeps_its_file_name_and_subfolder()
    {
        var store = Open();
        Directory.CreateDirectory(P("Work", "Clients"));
        await File.WriteAllTextAsync(P("Work", "Clients", "2024-05 Taxes.md"), "# Do taxes\n");
        var id = await store.ReadAsync(b => b.Items.Single().Id);
        var personal = await store.ReadAsync(b => b.Groups.Single(g => g.Name == "Personal").Id);

        await store.UpdateAsync(b => b.MoveToGroup(id, personal, Actor.User, T0));

        Assert.True(File.Exists(P("Personal", "Clients", "2024-05 Taxes.md")));
        Assert.Equal("Personal/Clients/2024-05 Taxes.md", store.PathOf(id));
    }

    [Fact]
    public async Task Renaming_a_group_keeps_links_to_files_inside_it_working()
    {
        var store = Open();
        await File.WriteAllTextAsync(P("Work", "diagram.png"), "png");
        await File.WriteAllTextAsync(P("Work", "Ship.md"), "# Ship\n\n## Attachments\n\n- [diagram.png](diagram.png)\n");
        var (id, attachment) = await store.ReadAsync(b => (b.Items.Single().Id, b.Items.Single().Attachments.Single().Id));
        var work = await store.ReadAsync(b => b.Groups.Single(g => g.Name == "Work").Id);

        await store.UpdateAsync(b => b.UpdateGroup(work, "Office", null, Actor.User, T0));

        var (full, _) = await store.GetAttachmentFileAsync(id, attachment);
        Assert.Equal("png", await File.ReadAllTextAsync(full));
    }

    [Fact]
    public async Task Deleting_a_group_keeps_other_files_from_its_folder()
    {
        var store = Open();
        var personal = await store.ReadAsync(b => b.Groups.Single(g => g.Name == "Personal").Id);
        await File.WriteAllTextAsync(P("Personal", "photo.png"), "png");
        await File.WriteAllTextAsync(P("Personal", "Trip.md"), "# Trip\n\n## Attachments\n\n- [photo.png](photo.png)\n");
        var (id, attachment) = await store.ReadAsync(b => (b.Items.Single().Id, b.Items.Single().Attachments.Single().Id));
        var work = await store.ReadAsync(b => b.Groups.Single(g => g.Name == "Work").Id);

        await store.UpdateAsync(b => b.DeleteGroup(personal, work, Actor.User, T0));

        Assert.False(Directory.Exists(P("Personal")));
        Assert.True(File.Exists(P("Work", "Trip.md")));
        Assert.Equal("Work/Trip.md", store.PathOf(id));
        var (full, _) = await store.GetAttachmentFileAsync(id, attachment);
        Assert.Equal("png", await File.ReadAllTextAsync(full));
    }

    [Fact]
    public async Task Group_names_with_a_leading_underscore_still_get_a_visible_folder()
    {
        var store = Open();

        var group = await store.UpdateAsync(b => b.AddGroup("_Someday", null, Actor.User, T0));
        await Add(store, "Learn piano", group: group.Id);
        store.Dispose();

        Assert.Contains("Learn piano", await Open().ReadAsync(b => b.Items.Select(i => i.Title).ToList()));
    }

    [Fact]
    public async Task The_order_of_top_level_tasks_is_remembered()
    {
        var store = Open();
        await Add(store, "First");
        await Add(store, "Second");
        var third = await Add(store, "Third");

        await store.UpdateAsync(b => b.Move(third.Id, null, 0, null, Actor.User, T0));
        store.Dispose();

        Assert.Equal(["Third", "First", "Second"], await Open().ReadAsync(b => b.Items.Select(i => i.Title).ToList()));
    }

    [Fact]
    public async Task Turning_a_task_with_a_rich_version_into_a_subtask_is_refused()
    {
        var store = Open();
        var project = await Add(store, "Project");
        var task = await Add(store, "Plan");
        await store.WriteRichAsync(task.Id, "<p>x</p>", Actor.User);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(b => b.Move(task.Id, project.Id, null, null, Actor.User, T0)));
        Assert.True(File.Exists(P("Work", "Plan.html")));
    }

    [Fact]
    public async Task Two_attachments_with_the_same_name_at_once_both_survive()
    {
        var store = Open();
        var task = await Add(store, "Ship");

        var all = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => store.AddAttachmentAsync(task.Id, "image.png", new MemoryStream([(byte)i]), Actor.User)));

        Assert.Equal(4, all.Select(a => a.Path).Distinct().Count());
        foreach (var a in all)
        {
            var (full, _) = await store.GetAttachmentFileAsync(task.Id, a.Id);
            Assert.True(File.Exists(full));
        }
    }

    [Fact]
    public async Task Removing_an_attachment_never_deletes_files_outside_the_attachments_folder()
    {
        var store = Open();
        var other = await Add(store, "Important");
        await File.WriteAllTextAsync(P("Work", "Ship.md"), "# Ship\n\n## Attachments\n\n- ![[_attachments/../Work/Important.md]]\n");
        var (id, attachment) = await store.ReadAsync(b => b.Items.Where(i => i.Title == "Ship").Select(i => (i.Id, i.Attachments.Single().Id)).Single());

        await store.RemoveAttachmentAsync(id, attachment, Actor.User);

        Assert.True(File.Exists(P("Work", "Important.md")));
        Assert.True(await store.ReadAsync(b => b.Find(other.Id) is not null));
    }

    [Fact]
    public async Task A_torn_last_activity_line_does_not_swallow_the_next_entry()
    {
        var store = Open();
        await Add(store, "A");
        await File.AppendAllTextAsync(P(".todo-tracker", "activity.jsonl"), "{\"at\":\"2026-01-05T");

        var b = await Add(store, "B");
        store.Dispose();

        Assert.Contains(await Open().ReadAsync(x => x.Activity.ToList()), e => e.ItemId == b.Id && e.Kind == ActivityKind.Created);
    }
}
