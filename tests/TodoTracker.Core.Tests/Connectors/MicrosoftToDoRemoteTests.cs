using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TodoTracker.Core.Connectors;

namespace TodoTracker.Core.Tests.Connectors;

public sealed class MicrosoftToDoRemoteTests : IDisposable
{
    private readonly FakeGraph _graph = new();
    private readonly HttpClient _http;

    public MicrosoftToDoRemoteTests()
    {
        _http = new HttpClient(_graph, disposeHandler: false) { BaseAddress = new Uri("https://graph.microsoft.com/") };
    }

    public void Dispose()
    {
        _http.Dispose();
        _graph.Dispose();
    }

    private MicrosoftToDoRemote Remote(string token = "graph-token", TimeZoneInfo? zone = null) => new(_http, _ => Task.FromResult(token), FakeGraph.List, zone ?? TimeZoneInfo.Utc);

    [Fact]
    public async Task Lists_are_offered_by_name()
    {
        var lists = await MicrosoftToDoRemote.ListsAsync(_http, _ => Task.FromResult("graph-token"), CancellationToken.None);

        Assert.Equal([("L1", "Tasks"), ("L2", "Groceries")], lists.Select(l => (l.Id, l.Name)));
    }

    [Fact]
    public async Task Tasks_are_read_across_pages_with_their_marks_dates_and_notes()
    {
        var a = Guid.NewGuid();
        _graph.Add("t1", "Call the bank", importance: "high", due: "2026-01-08", body: "Ask about fees", marker: a.ToString());
        _graph.Add("t2", "Done thing", status: "completed", importance: "low");
        _graph.Add("t3", "Rich note", body: "<p>Line <b>one</b></p><p>two &amp; three</p>", contentType: "html");

        var items = await Remote().ListAsync(CancellationToken.None);

        Assert.Equal(["t1", "t2", "t3"], items.Select(i => i.Id));
        Assert.Equal(new SyncedFields("Call the bank", false, new DateOnly(2026, 1, 8), Priority.High, [], "Ask about fees"), items[0].Fields);
        Assert.Equal(a, items[0].Marker);
        Assert.Equal((true, Priority.Low), (items[1].Fields.Done, items[1].Fields.Priority));
        Assert.Equal("Line one\ntwo & three", items[2].Fields.Notes);
        Assert.Contains(_graph.Requests, r => r.Path.Contains("$expand=linkedResources", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Creating_updating_marking_and_finishing_write_the_task()
    {
        var remote = Remote();
        var id = Guid.NewGuid();

        var created = await remote.CreateAsync(id, new SyncedFields("Write report", false, new DateOnly(2026, 2, 1), Priority.Critical, ["ignored"], "Draft first"), CancellationToken.None);

        var task = _graph.Tasks[created];
        Assert.Equal(("Write report", "high", "notStarted"), (task["title"]!.GetValue<string>(), task["importance"]!.GetValue<string>(), task["status"]!.GetValue<string>()));
        // Midday UTC: the same day in every time zone people live in.
        Assert.Equal("2026-02-01T12:00:00", task["dueDateTime"]!["dateTime"]!.GetValue<string>());
        Assert.Equal("Draft first", task["body"]!["content"]!.GetValue<string>());
        Assert.Equal(id.ToString(), task["linkedResources"]![0]!["externalId"]!.GetValue<string>());

        var sent = new SyncedFields("Write report", false, new DateOnly(2026, 2, 1), Priority.Critical, [], "Draft first");
        await remote.UpdateAsync(created, sent with { Done = true, Due = null, Priority = Priority.Normal }, sent, CancellationToken.None);
        Assert.Equal("completed", task["status"]!.GetValue<string>());
        Assert.Null(task["dueDateTime"]);
        Assert.DoesNotContain(_graph.Requests, r => r.Method == HttpMethod.Get && r.Path.Contains(created, StringComparison.Ordinal)); // no read before the write

        var other = Guid.NewGuid();
        await remote.MarkAsync(created, other, CancellationToken.None);
        Assert.Equal(other, (await remote.FindAsync(created, CancellationToken.None))!.Marker);

        await remote.ArchiveAsync(created, CancellationToken.None);
        Assert.Equal("completed", task["status"]!.GetValue<string>());
        Assert.Null(await remote.FindAsync("gone", CancellationToken.None));
    }

    [Fact]
    public async Task Due_dates_are_days_in_the_users_time_zone_and_in_progress_stays_on_a_rename()
    {
        // Set in the To Do app for 8 January by someone two hours ahead of UTC: Graph says 7 January, 22:00 UTC.
        var task = _graph.Add("t1", "Plan", status: "inProgress");
        task["dueDateTime"] = new JsonObject { ["dateTime"] = "2026-01-07T22:00:00.0000000", ["timeZone"] = "UTC" };
        var remote = Remote(zone: TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2"));

        var item = Assert.Single(await remote.ListAsync(CancellationToken.None));
        Assert.Equal(new DateOnly(2026, 1, 8), item.Fields.Due);

        await remote.UpdateAsync("t1", item.Fields with { Title = "Plan the trip" }, item.Fields, CancellationToken.None);
        Assert.Equal("inProgress", task["status"]!.GetValue<string>());
        Assert.Equal("2026-01-07T22:00:00.0000000", task["dueDateTime"]!["dateTime"]!.GetValue<string>()); // untouched
    }

    [Fact]
    public void Critical_shows_as_high_there()
    {
        var f = new SyncedFields("x", false, null, Priority.Critical, [], null);
        Assert.Equal(Priority.High, ((IConnectorRemote)Remote()).Normalize(f).Priority);
        Assert.Equal(ConnectorFields.Title | ConnectorFields.Done | ConnectorFields.Due | ConnectorFields.Priority | ConnectorFields.Notes, Remote().Fields);
    }

    [Fact]
    public async Task Graph_errors_say_what_went_wrong()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Remote("bad").ListAsync(CancellationToken.None));
        Assert.Contains("Access token is empty", ex.Message, StringComparison.Ordinal);
    }
}
