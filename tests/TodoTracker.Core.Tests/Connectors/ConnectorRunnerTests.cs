using Microsoft.Extensions.Time.Testing;
using TodoTracker.Core.Connectors;

namespace TodoTracker.Core.Tests;

/// <summary>A connector run end to end: the board here, a fake outside list, and the links in between.</summary>
public sealed class ConnectorRunnerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
    private readonly InMemoryBoardStore _store = new();
    private readonly FakeTimeProvider _time = new(T0);
    private readonly FakeRemote _remote = new();
    private readonly ConnectorRunner _runner;
    private IReadOnlyList<ConnectorLink> _links = [];
    private Guid _group;

    public ConnectorRunnerTests()
    {
        _runner = new ConnectorRunner("notion", "Notion", _store, _time, TimeZoneInfo.Utc);
        _group = _store.UpdateAsync(b => b.AddGroup("Synced", null, Actor.User, T0).Id).Result;
    }

    public void Dispose() => _store.Dispose();

    private ConnectorTarget Target => new(_group, ConnectorDirection.Both);

    private async Task<ConnectorRun> RunAsync()
    {
        var run = await _runner.RunAsync(_remote, Target, _links, CancellationToken.None);
        _links = run.Links;
        return run;
    }

    private Task<WorkItem> AddAsync(string title, DateTimeOffset? deadline = null) =>
        _store.UpdateAsync(b => b.AddTask(new NewTask(title) { GroupId = _group, Deadline = deadline }, Actor.User, T0));

    private static SyncedFields F(string title, bool done = false, DateOnly? due = null, Priority priority = Priority.Normal) => new(title, done, due, priority, [], null);

    [Fact]
    public async Task Outside_items_come_in_to_the_group_and_tasks_here_go_out_once()
    {
        _remote.Add("r1", F("Call the bank", due: new DateOnly(2026, 1, 8), priority: Priority.High));
        var mine = await AddAsync("Write report");

        var first = await RunAsync();

        Assert.Empty(first.Problems);
        var imported = await _store.ReadAsync(b => b.Find(ConnectorPlan.ImportId("notion", "r1")));
        Assert.NotNull(imported);
        Assert.Equal(("Call the bank", _group, Priority.High), (imported.Title, imported.GroupId, imported.Priority));
        Assert.Equal(new DateTimeOffset(2026, 1, 8, 17, 0, 0, TimeSpan.Zero), imported.Deadline);
        Assert.Equal(ActorKind.Connector, (await _store.ReadAsync(b => b.Activity.First(a => a.ItemId == imported.Id))).Actor.Kind);
        Assert.Equal(imported.Id, _remote.Items["r1"].Marker);
        var sent = Assert.Single(_remote.Items.Values, r => r.Marker == mine.Id);
        Assert.Equal("Write report", sent.Fields.Title);

        var second = await RunAsync();

        Assert.Empty(second.Plan.Ops);
        Assert.Equal(2, _remote.Items.Count);
        Assert.Equal(2, await _store.ReadAsync(b => b.Items.Count));
    }

    [Fact]
    public async Task Changes_flow_both_ways_finishing_included()
    {
        var task = await AddAsync("Report", new DateTimeOffset(2026, 1, 9, 17, 0, 0, TimeSpan.Zero));
        await RunAsync();
        var remoteId = _remote.Items.Single().Key;

        // There: renamed and finished. Here: the deadline moved (its time of day here is kept).
        _remote.Items[remoteId] = _remote.Items[remoteId] with { Fields = F("Quarterly report", done: true, due: new DateOnly(2026, 1, 9)) };
        await _store.UpdateAsync(b => { b.Update(task.Id, new TaskChanges { Deadline = new DateTimeOffset(2026, 1, 12, 9, 30, 0, TimeSpan.Zero) }, Actor.User, T0); return 0; });

        await RunAsync();

        var here = await _store.ReadAsync(b => b.Find(task.Id)!);
        Assert.Equal("Quarterly report", here.Title);
        Assert.NotNull(here.CompletedAt);
        Assert.Equal(new DateTimeOffset(2026, 1, 12, 9, 30, 0, TimeSpan.Zero), here.Deadline);
        Assert.Equal(F("Quarterly report", done: true, due: new DateOnly(2026, 1, 12)), _remote.Items[remoteId].Fields);

        // Reopened here: open there too.
        await _store.UpdateAsync(b => { b.Reopen(task.Id, Actor.User, T0); return 0; });
        await RunAsync();
        Assert.False(_remote.Items[remoteId].Fields.Done);
    }

    [Fact]
    public async Task A_task_deleted_here_is_archived_there_and_a_gone_item_leaves_the_task_alone()
    {
        var keep = await AddAsync("Keep me");
        var drop = await AddAsync("Drop me");
        await RunAsync();
        var keepRemote = _remote.Items.Single(r => r.Value.Marker == keep.Id).Key;
        var dropRemote = _remote.Items.Single(r => r.Value.Marker == drop.Id).Key;

        await _store.UpdateAsync(b => { b.Delete(drop.Id, Actor.User, T0); return 0; });
        _remote.Items.Remove(keepRemote); // deleted in Notion

        var run = await RunAsync();

        Assert.Contains(dropRemote, _remote.Archived);
        Assert.NotNull(await _store.ReadAsync(b => b.Find(keep.Id)));
        Assert.Empty(run.Problems);
        Assert.Contains(_links, l => l.LocalId == keep.Id && l.State == LinkState.RemoteGone);
    }

    [Fact]
    public async Task A_failed_change_is_reported_and_tried_again_next_time()
    {
        var task = await AddAsync("Report");
        await RunAsync();
        await _store.UpdateAsync(b => { b.Update(task.Id, new TaskChanges { Title = "Report v2" }, Actor.User, T0); return 0; });
        _remote.FailUpdates = true;

        var failed = await RunAsync();

        Assert.Single(failed.Problems);
        Assert.Equal(1, failed.Failed);
        _remote.FailUpdates = false;
        await RunAsync();
        Assert.Equal("Report v2", _remote.Items.Single().Value.Fields.Title);
    }

    [Fact]
    public async Task Only_open_or_recently_finished_top_level_tasks_of_the_group_go_out()
    {
        var parent = await AddAsync("Parent");
        await _store.UpdateAsync(b => b.AddTask(new NewTask("Child") { ParentId = parent.Id }, Actor.User, T0));
        await _store.UpdateAsync(b => b.AddTask(new NewTask("Elsewhere"), Actor.User, T0));
        var old = await AddAsync("Finished long ago");
        await _store.UpdateAsync(b => { b.Complete(old.Id, Actor.User, T0.AddDays(-30)); return 0; });

        await RunAsync();

        Assert.Equal(["Parent"], _remote.Items.Values.Select(r => r.Fields.Title));
    }

    [Fact]
    public async Task A_linked_item_missing_from_the_list_is_looked_up_before_it_counts_as_gone()
    {
        var task = await AddAsync("Report");
        await RunAsync();
        var remoteId = _remote.Items.Single().Key;
        _remote.HiddenFromList.Add(remoteId); // e.g. filtered out of the database view

        var run = await RunAsync();

        Assert.Empty(run.Plan.Ops);
        Assert.Contains(_links, l => l.LocalId == task.Id && l.State == LinkState.Active);
    }

    private sealed class FakeRemote : IConnectorRemote
    {
        private int _next;

        public Dictionary<string, RemoteItem> Items { get; } = [];

        public HashSet<string> Archived { get; } = [];

        public HashSet<string> HiddenFromList { get; } = [];

        public bool FailUpdates { get; set; }

        public ConnectorFields Fields => ConnectorFields.Title | ConnectorFields.Done | ConnectorFields.Due | ConnectorFields.Priority;

        public void Add(string id, SyncedFields fields) => Items[id] = new RemoteItem(id, fields, null);

        public Task<IReadOnlyList<RemoteItem>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteItem>>([.. Items.Values.Where(r => !Archived.Contains(r.Id) && !HiddenFromList.Contains(r.Id))]);

        public Task<RemoteItem?> FindAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(Items.TryGetValue(id, out var item) && !Archived.Contains(id) ? item : null);

        public Task<string> CreateAsync(Guid localId, SyncedFields fields, CancellationToken cancellationToken)
        {
            var id = $"new{++_next}";
            Items[id] = new RemoteItem(id, fields, localId);
            return Task.FromResult(id);
        }

        public Task UpdateAsync(string id, SyncedFields fields, CancellationToken cancellationToken)
        {
            if (FailUpdates)
            {
                throw new HttpRequestException("Notion is down");
            }

            Items[id] = Items[id] with { Fields = fields };
            return Task.CompletedTask;
        }

        public Task ArchiveAsync(string id, CancellationToken cancellationToken)
        {
            Archived.Add(id);
            return Task.CompletedTask;
        }

        public Task MarkAsync(string id, Guid localId, CancellationToken cancellationToken)
        {
            Items[id] = Items[id] with { Marker = localId };
            return Task.CompletedTask;
        }
    }
}
