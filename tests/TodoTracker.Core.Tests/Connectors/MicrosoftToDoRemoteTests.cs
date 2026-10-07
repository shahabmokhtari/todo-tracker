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

    private MicrosoftToDoRemote Remote(string token = "graph-token") => new(_http, _ => Task.FromResult(token), FakeGraph.List);

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
        Assert.Equal("2026-02-01T00:00:00", task["dueDateTime"]!["dateTime"]!.GetValue<string>());
        Assert.Equal("Draft first", task["body"]!["content"]!.GetValue<string>());
        Assert.Equal(id.ToString(), task["linkedResources"]![0]!["externalId"]!.GetValue<string>());

        await remote.UpdateAsync(created, new SyncedFields("Write report", true, null, Priority.Normal, [], null), CancellationToken.None);
        Assert.Equal("completed", task["status"]!.GetValue<string>());
        Assert.Null(task["dueDateTime"]);

        var other = Guid.NewGuid();
        await remote.MarkAsync(created, other, CancellationToken.None);
        Assert.Equal(other, (await remote.FindAsync(created, CancellationToken.None))!.Marker);

        await remote.ArchiveAsync(created, CancellationToken.None);
        Assert.Equal("completed", task["status"]!.GetValue<string>());
        Assert.Null(await remote.FindAsync("gone", CancellationToken.None));
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
