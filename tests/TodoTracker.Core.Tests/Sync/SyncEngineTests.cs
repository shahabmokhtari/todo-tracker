using TodoTracker.Core.Sync;
using TodoTracker.Core.Vault;
using Xunit;

namespace TodoTracker.Core.Tests.Sync;

/// <summary>Two (or three) devices, each with its own vault, syncing through one shared folder.</summary>
public sealed class SyncEngineTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
    private readonly string _root = Directory.CreateTempSubdirectory("tt-sync-").FullName;
    private readonly List<Device> _devices = [];

    private string Remote => Path.Combine(_root, "remote");

    public void Dispose()
    {
        foreach (var device in _devices)
        {
            device.Engine.Dispose();
            device.Store.Dispose();
        }

        Directory.Delete(_root, recursive: true);
    }

    private Device Add(string name)
    {
        var device = new Device(_root, name, Remote);
        _devices.Add(device);
        return device;
    }

    private sealed class Device
    {
        public Device(string root, string name, string remote, string? displayName = null)
        {
            var home = Path.Combine(root, name);
            Vault = Path.Combine(home, "vault");
            Store = VaultBoardStore.Open(new VaultOptions(Vault) { TimeZone = TimeZoneInfo.Utc, LockDirectory = Path.Combine(home, "locks"), Watch = false, EditSettleTime = TimeSpan.Zero });
            State = new SyncState(Path.Combine(home, "sync"), Path.Combine(home, "device"), displayName ?? name);
            Engine = new SyncEngine(Store, new FolderRemote(remote), State, Path.Combine(home, "sync.lock"));
        }

        public string Vault { get; }

        public VaultBoardStore Store { get; }

        public SyncState State { get; }

        public SyncEngine Engine { get; }

        public Task<SyncResult> Sync() => Engine.SyncAsync(TestContext.Current.CancellationToken);

        public Task<WorkItem> AddTask(string title, Guid? parent = null) =>
            Store.UpdateAsync(b => b.AddTask(new NewTask(title) { ParentId = parent }, Actor.User, T0));

        public Task<List<string>> Titles() => Store.ReadAsync(b => b.AllItems().Select(i => i.Title).Order(StringComparer.Ordinal).ToList());

        public Task<WorkItem?> Find(string title) => Store.ReadAsync(b => b.AllItems().FirstOrDefault(i => i.Title == title));

        public string File(string title) => Path.Combine(Vault, Store.PathOf(Find(title).Result!.Id)!);
    }

    /// <summary>Syncs every device in turn until nothing changes (at most a few rounds).</summary>
    private async Task Settle()
    {
        for (var round = 0; round < 4; round++)
        {
            var changes = 0;
            foreach (var device in _devices)
            {
                var result = await device.Sync();
                changes += result.Changes + (result.Published ? 1 : 0);
            }

            if (changes == 0)
            {
                return;
            }
        }

        Assert.Fail("Sync didn't settle.");
    }

    [Fact]
    public async Task A_task_added_on_one_device_shows_up_on_the_other()
    {
        var laptop = Add("Laptop");
        var desktop = Add("Desktop");
        await laptop.AddTask("Book flights");

        await laptop.Sync();
        await desktop.Sync();

        Assert.Contains("Book flights", await desktop.Titles());
    }

    [Fact]
    public async Task Changes_on_both_devices_end_up_on_both_and_the_files_match()
    {
        var laptop = Add("Laptop");
        var desktop = Add("Desktop");
        var trip = await laptop.AddTask("Plan trip");
        await laptop.AddTask("Flights", trip.Id);
        await laptop.AddTask("Hotel", trip.Id);
        await Settle();

        var flights = (await laptop.Find("Flights"))!;
        await laptop.Store.UpdateAsync(b => b.Complete(flights.Id, Actor.User, T0));
        var hotel = (await desktop.Find("Hotel"))!;
        await desktop.Store.UpdateAsync(b => b.Update(hotel.Id, new TaskChanges { Title = "Hotel near the beach" }, Actor.User, T0));
        await desktop.AddTask("Desktop only");
        await Settle();

        foreach (var device in _devices)
        {
            Assert.True((await device.Find("Flights"))!.IsDone);
            Assert.NotNull(await device.Find("Hotel near the beach"));
            Assert.NotNull(await device.Find("Desktop only"));
        }

        Assert.Equal(System.IO.File.ReadAllText(laptop.File("Plan trip")), System.IO.File.ReadAllText(desktop.File("Plan trip")));
    }

    [Fact]
    public async Task A_completion_from_another_device_is_logged_once()
    {
        var laptop = Add("Laptop");
        var desktop = Add("Desktop");
        var task = await laptop.AddTask("Pay rent");
        await Settle();

        await laptop.Store.UpdateAsync(b => b.Complete(task.Id, Actor.User, T0));
        await Settle();

        foreach (var device in _devices)
        {
            var completions = await device.Store.ReadAsync(b => b.Activity.Count(a => a.ItemId == task.Id && a.Kind == ActivityKind.Completed));
            Assert.Equal(1, completions);
        }
    }

    [Fact]
    public async Task Clashing_edits_keep_both_versions_and_are_reported()
    {
        var laptop = Add("Laptop");
        var desktop = Add("Desktop");
        var task = await laptop.AddTask("Call mom");
        await Settle();

        await laptop.Store.UpdateAsync(b => b.AddNote(task.Id, "Ask about Sunday", Actor.User, T0));
        await desktop.Store.UpdateAsync(b => b.AddNote(task.Id, "Ask about the car", Actor.User, T0));
        await Settle();

        var text = System.IO.File.ReadAllText(laptop.File("Call mom"));
        Assert.Contains("Ask about Sunday", text, StringComparison.Ordinal);
        Assert.Contains("Ask about the car", text, StringComparison.Ordinal);
        Assert.Equal(text, System.IO.File.ReadAllText(desktop.File("Call mom")));
    }

    [Fact]
    public async Task A_task_deleted_on_one_device_goes_to_the_trash_on_the_other()
    {
        var laptop = Add("Laptop");
        var desktop = Add("Desktop");
        var task = await laptop.AddTask("Old idea");
        await Settle();

        await laptop.Store.UpdateAsync(b => b.Delete(task.Id, Actor.User, T0));
        await Settle();

        Assert.DoesNotContain("Old idea", await desktop.Titles());
        Assert.Contains(Directory.EnumerateFiles(Path.Combine(desktop.Vault, ".todo-tracker", "trash")), f => f.EndsWith("Old idea.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Moving_a_task_to_another_tab_moves_it_everywhere_and_keeps_edits()
    {
        var laptop = Add("Laptop");
        var desktop = Add("Desktop");
        var task = await laptop.AddTask("Renew passport");
        await laptop.AddTask("Photos", task.Id);
        await Settle();

        var personal = await laptop.Store.ReadAsync(b => b.Groups.Single(g => g.Name == "Personal").Id);
        await laptop.Store.UpdateAsync(b => b.MoveToGroup(task.Id, personal, Actor.User, T0));
        var photos = (await desktop.Find("Photos"))!;
        await desktop.Store.UpdateAsync(b => b.Complete(photos.Id, Actor.User, T0));
        await Settle();

        foreach (var device in _devices)
        {
            Assert.StartsWith("Personal/", device.Store.PathOf(task.Id), StringComparison.Ordinal);
            Assert.True((await device.Find("Photos"))!.IsDone);
        }
    }

    [Fact]
    public async Task Two_new_vaults_share_their_default_tabs()
    {
        var laptop = Add("Laptop");
        var desktop = Add("Desktop");
        await laptop.AddTask("One");
        await desktop.AddTask("Two");

        await Settle();

        foreach (var device in _devices)
        {
            Assert.Equal(["Work", "Personal"], await device.Store.ReadAsync(b => b.Groups.Select(g => g.Name).ToList()));
            Assert.Equal(["One", "Two"], await device.Titles());
        }
    }

    [Fact]
    public async Task The_now_order_follows_the_last_change()
    {
        var laptop = Add("Laptop");
        var desktop = Add("Desktop");
        var a = await laptop.AddTask("Alpha");
        var b = await laptop.AddTask("Bravo");
        await Settle();

        await desktop.Store.UpdateAsync(x => { x.PutFirst([b.Id], T0); return 0; });
        await Settle();

        Assert.Equal(b.Id, await laptop.Store.ReadAsync(x => x.NowOrder.Count > 0 ? x.NowOrder[0] : Guid.Empty));
    }

    [Fact]
    public async Task Nothing_changed_means_nothing_is_merged_or_published()
    {
        var laptop = Add("Laptop");
        Add("Desktop");
        await laptop.AddTask("Something");
        await Settle();

        var again = await laptop.Sync();

        Assert.True(again.Skipped);
        Assert.False(again.Published);
    }

    [Fact]
    public async Task Three_devices_agree()
    {
        var a = Add("A");
        var b = Add("B");
        var c = Add("C");
        await a.AddTask("From A");
        await b.AddTask("From B");
        await c.AddTask("From C");

        await Settle();

        foreach (var device in _devices)
        {
            Assert.Equal(["From A", "From B", "From C"], await device.Titles());
        }
    }

    [Fact]
    public async Task A_device_whose_contents_have_not_arrived_is_merged_later()
    {
        var laptop = Add("Laptop");
        var desktop = Add("Desktop");
        await laptop.AddTask("Slow upload");
        await laptop.Sync();
        var blobs = Directory.EnumerateFiles(Path.Combine(Remote, "blobs"), "*", SearchOption.AllDirectories).ToList();
        var hidden = blobs.ToDictionary(f => f, System.IO.File.ReadAllBytes);
        blobs.ForEach(System.IO.File.Delete);

        await desktop.Sync();
        Assert.DoesNotContain("Slow upload", await desktop.Titles());

        foreach (var (path, bytes) in hidden)
        {
            await System.IO.File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        }

        await desktop.Sync();
        Assert.Contains("Slow upload", await desktop.Titles());
    }

    [Fact]
    public async Task Paths_from_another_device_never_leave_the_vault()
    {
        var desktop = Add("Desktop");
        var evil = new DeviceSnapshot("evil0000000000000000000000000000", "Evil", T0, [new SyncEntry("f/../outside.txt", "../outside.txt", SyncHash.Of("x"u8.ToArray()))]);
        await new FolderRemote(Remote).PublishAsync(evil, _ => "x"u8.ToArray(), null, TestContext.Current.CancellationToken);

        await desktop.Sync();

        Assert.False(System.IO.File.Exists(Path.Combine(_root, "Desktop", "outside.txt")));
    }

    [Fact]
    public async Task A_new_device_does_not_bring_back_what_was_deleted_while_another_device_was_away()
    {
        // Review finding: a stale device's old snapshot re-created deleted tasks on a device joining later.
        var laptop = Add("Laptop");
        var desktop = Add("Desktop");
        var task = await laptop.AddTask("Cancelled trip");
        await Settle();
        _devices.Remove(desktop);

        await laptop.Store.UpdateAsync(b => b.Delete(task.Id, Actor.User, T0));
        await laptop.Sync();
        var phone = Add("Phone");
        await phone.Sync();
        await laptop.Sync();

        Assert.DoesNotContain("Cancelled trip", await phone.Titles());
        Assert.DoesNotContain("Cancelled trip", await laptop.Titles());
        desktop.Engine.Dispose();
        desktop.Store.Dispose();
    }

    [Fact]
    public async Task Links_inside_the_vault_that_lead_elsewhere_are_not_synced()
    {
        var laptop = Add("Laptop");
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        await System.IO.File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "private", TestContext.Current.CancellationToken);
        var link = Path.Combine(laptop.Vault, "Work", "linked");
        if (OperatingSystem.IsWindows())
        {
            using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            await mklink.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        else
        {
            Directory.CreateSymbolicLink(link, outside);
        }

        Assert.True(Directory.Exists(link));
        await laptop.AddTask("Something");
        await laptop.Sync();

        var published = Directory.EnumerateFiles(Path.Combine(Remote, "blobs"), "*", SearchOption.AllDirectories).Select(System.IO.File.ReadAllText).ToList();
        Directory.Delete(link);
        Assert.DoesNotContain("private", published);
    }

    [Fact]
    public async Task The_same_file_added_again_after_a_delete_stays_everywhere()
    {
        // Review finding: a delete recorded earlier deleted the same content added again later.
        var laptop = Add("Laptop");
        var desktop = Add("Desktop");
        var notes = Path.Combine(laptop.Vault, "Work", "notes.txt");
        await System.IO.File.WriteAllTextAsync(notes, "same text", TestContext.Current.CancellationToken);
        await Settle();
        System.IO.File.Delete(notes);
        laptop.Store.MarkDirty();
        await Settle();
        Assert.False(System.IO.File.Exists(Path.Combine(desktop.Vault, "Work", "notes.txt")));

        await System.IO.File.WriteAllTextAsync(notes, "same text", TestContext.Current.CancellationToken);
        laptop.Store.MarkDirty();
        await Settle();
        await desktop.AddTask("Unrelated");
        await Settle();

        Assert.True(System.IO.File.Exists(notes));
        Assert.True(System.IO.File.Exists(Path.Combine(desktop.Vault, "Work", "notes.txt")));
    }

    [Fact]
    public async Task A_file_renamed_only_in_letter_case_is_not_duplicated()
    {
        var laptop = Add("Laptop");
        var desktop = Add("Desktop");
        var lower = Path.Combine(laptop.Vault, "Work", "pic.txt");
        await System.IO.File.WriteAllTextAsync(lower, "x", TestContext.Current.CancellationToken);
        await Settle();

        System.IO.File.Move(lower, Path.Combine(laptop.Vault, "Work", "Pic.txt"));
        laptop.Store.MarkDirty();
        await Settle();

        Assert.Equal(["Pic.txt"], Directory.EnumerateFiles(Path.Combine(desktop.Vault, "Work"), "*.txt").Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_new_device_with_an_old_copy_of_a_deleted_file_does_not_bring_it_back()
    {
        // Review finding: a device with no sync history (a restored backup, a reinstall) counted its files as new.
        var laptop = Add("Laptop");
        var desktop = Add("Desktop");
        var notes = Path.Combine(laptop.Vault, "Work", "old.txt");
        await System.IO.File.WriteAllTextAsync(notes, "old notes", TestContext.Current.CancellationToken);
        laptop.Store.MarkDirty();
        await Settle();
        System.IO.File.Delete(notes);
        laptop.Store.MarkDirty();
        await Settle();

        var restored = Add("Restored");
        await System.IO.File.WriteAllTextAsync(Path.Combine(restored.Vault, "Work", "old.txt"), "old notes", TestContext.Current.CancellationToken);
        restored.Store.MarkDirty();
        await Settle();

        Assert.All(_devices, d => Assert.False(System.IO.File.Exists(Path.Combine(d.Vault, "Work", "old.txt")), d.State.DeviceName));
    }

    [Fact]
    public async Task A_device_name_that_is_not_a_file_name_still_syncs()
    {
        // Review finding: a copy named "(from Mac: home)" was refused, and that stopped the sync for good.
        var laptop = Add("Laptop");
        var mac = new Device(_root, "Mac", Remote, displayName: "Mac: home/x");
        _devices.Add(mac);
        await System.IO.File.WriteAllTextAsync(Path.Combine(laptop.Vault, "Work", "n.txt"), "laptop", TestContext.Current.CancellationToken);
        await System.IO.File.WriteAllTextAsync(Path.Combine(mac.Vault, "Work", "n.txt"), "mac", TestContext.Current.CancellationToken);
        laptop.Store.MarkDirty();
        mac.Store.MarkDirty();

        await Settle();

        var names = Directory.EnumerateFiles(Path.Combine(laptop.Vault, "Work"), "n*.txt").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.True(names.Count == 2, string.Join(" | ", names) + " || mac: " + string.Join(" | ", Directory.EnumerateFiles(Path.Combine(mac.Vault, "Work"), "n*.txt").Select(Path.GetFileName)));
        Assert.Equal(names, Directory.EnumerateFiles(Path.Combine(mac.Vault, "Work"), "n*.txt").Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_device_whose_own_file_went_missing_publishes_again()
    {
        var laptop = Add("Laptop");
        await laptop.AddTask("Still here");
        await laptop.Sync();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Remote, "devices")))
        {
            System.IO.File.Delete(file);
        }

        var again = await laptop.Sync();

        Assert.True(again.Published);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(Remote, "devices")));
    }

    [Fact]
    public async Task A_forgotten_device_is_no_longer_merged()
    {
        var laptop = Add("Laptop");
        var old = Add("Old tasks folder");
        await old.AddTask("From the old folder");
        await old.Sync();
        _devices.Remove(old);

        await laptop.Engine.ForgetAsync(old.State.DeviceId, TestContext.Current.CancellationToken);
        await laptop.Sync();

        Assert.DoesNotContain("From the old folder", await laptop.Titles());
        old.Engine.Dispose();
        old.Store.Dispose();
    }

    [Fact]
    public async Task Attachments_sync_as_files()
    {
        var laptop = Add("Laptop");
        var desktop = Add("Desktop");
        var task = await laptop.AddTask("Tax return");
        await laptop.Store.AddAttachmentAsync(task.Id, "receipt.txt", new MemoryStream("paid"u8.ToArray()), Actor.User, TestContext.Current.CancellationToken);
        await Settle();

        var attachment = Directory.EnumerateFiles(Path.Combine(desktop.Vault, "_attachments"), "receipt.txt", SearchOption.AllDirectories).Single();
        Assert.Equal("paid", System.IO.File.ReadAllText(attachment));
    }
}
