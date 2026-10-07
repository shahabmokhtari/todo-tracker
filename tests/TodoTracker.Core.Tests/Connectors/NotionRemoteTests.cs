using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TodoTracker.Core.Connectors;

namespace TodoTracker.Core.Tests.Connectors;

public sealed class NotionRemoteTests : IDisposable
{
    private readonly FakeNotion _notion = new();

    public void Dispose() => _notion.Dispose();

    private NotionApi Api(string token = "secret_token") => new(new HttpClient(_notion, disposeHandler: false) { BaseAddress = new Uri("https://api.notion.com/") }, token) { MinInterval = TimeSpan.Zero };

    private static JsonObject Props(string title, bool done = false, string? due = null, string? priority = null, string[]? tags = null, string? marker = null)
    {
        var props = new JsonObject
        {
            ["Task"] = new JsonObject { ["type"] = "title", ["title"] = new JsonArray(new JsonObject { ["plain_text"] = title }) },
            ["Done"] = new JsonObject { ["type"] = "checkbox", ["checkbox"] = done },
            ["Due date"] = new JsonObject { ["type"] = "date", ["date"] = due is null ? null : new JsonObject { ["start"] = due } },
            ["Priority"] = new JsonObject { ["type"] = "select", ["select"] = priority is null ? null : new JsonObject { ["name"] = priority } },
            ["Tags"] = new JsonObject { ["type"] = "multi_select", ["multi_select"] = new JsonArray([.. (tags ?? []).Select(t => (JsonNode)new JsonObject { ["name"] = t })]) },
        };
        if (marker is not null)
        {
            props["Todo Tracker ID"] = new JsonObject { ["type"] = "rich_text", ["rich_text"] = new JsonArray(new JsonObject { ["plain_text"] = marker }) };
        }

        return props;
    }

    [Fact]
    public async Task Databases_shared_with_the_integration_are_listed()
    {
        var databases = await Api().DatabasesAsync(CancellationToken.None);

        Assert.Equal([new NotionDatabase(FakeNotion.Db, "Tasks")], databases);
    }

    [Fact]
    public async Task Connecting_reads_which_columns_mean_what_and_adds_the_id_column_once()
    {
        var remote = await NotionRemote.ConnectAsync(Api(), FakeNotion.Db, CancellationToken.None);

        Assert.Equal(ConnectorFields.Title | ConnectorFields.Done | ConnectorFields.Due | ConnectorFields.Priority | ConnectorFields.Tags, remote.Fields);
        Assert.Equal("rich_text", _notion.Database["properties"]!["Todo Tracker ID"]!["type"]!.GetValue<string>());
        await NotionRemote.ConnectAsync(Api(), FakeNotion.Db, CancellationToken.None);
        Assert.Single(_notion.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task Pages_are_read_across_result_pages_without_archived_ones()
    {
        var a = Guid.NewGuid();
        _notion.AddPage("p1", Props("Call the bank", due: "2026-01-08", priority: "🔥 High", tags: ["Home", "money"], marker: a.ToString()));
        _notion.AddPage("p2", Props("Done thing", done: true, due: "2026-01-09T10:30:00.000+02:00", priority: "Urgent"));
        _notion.AddPage("p3", Props("Plain"));
        _notion.AddPage("p4", Props("Archived"), archived: true);
        var remote = await NotionRemote.ConnectAsync(Api(), FakeNotion.Db, CancellationToken.None);

        var items = await remote.ListAsync(CancellationToken.None);

        Assert.Equal(["p1", "p2", "p3"], items.Select(i => i.Id));
        Assert.Equal(new SyncedFields("Call the bank", false, new DateOnly(2026, 1, 8), Priority.High, ["Home", "money"], null), items[0].Fields);
        Assert.Equal(a, items[0].Marker);
        Assert.Equal((true, new DateOnly(2026, 1, 9), Priority.Critical), (items[1].Fields.Done, items[1].Fields.Due, items[1].Fields.Priority));
        Assert.Equal(Priority.Normal, items[2].Fields.Priority);
        Assert.Null(items[2].Marker);
    }

    [Fact]
    public async Task Creating_updating_marking_and_archiving_write_the_right_columns()
    {
        var remote = await NotionRemote.ConnectAsync(Api(), FakeNotion.Db, CancellationToken.None);
        var id = Guid.NewGuid();

        var created = await remote.CreateAsync(id, new SyncedFields("Write report", false, new DateOnly(2026, 2, 1), Priority.Critical, ["work"], "not sent"), CancellationToken.None);
        var page = _notion.Pages[created];
        Assert.Equal("Write report", page["properties"]!["Task"]!["title"]![0]!["text"]!["content"]!.GetValue<string>());
        Assert.Equal("2026-02-01", page["properties"]!["Due date"]!["date"]!["start"]!.GetValue<string>());
        Assert.Equal("Critical", page["properties"]!["Priority"]!["select"]!["name"]!.GetValue<string>());
        Assert.Equal(id.ToString(), page["properties"]!["Todo Tracker ID"]!["rich_text"]![0]!["text"]!["content"]!.GetValue<string>());
        Assert.Equal(FakeNotion.Db, _notion.Requests.Last().Body!["parent"]!["database_id"]!.GetValue<string>());

        await remote.UpdateAsync(created, new SyncedFields("Write report", true, null, Priority.Low, [], null), CancellationToken.None);
        Assert.True(page["properties"]!["Done"]!["checkbox"]!.GetValue<bool>());
        Assert.Null(page["properties"]!["Due date"]!["date"]);
        Assert.Empty(page["properties"]!["Tags"]!["multi_select"]!.AsArray());

        var other = Guid.NewGuid();
        await remote.MarkAsync(created, other, CancellationToken.None);
        Assert.Equal(other, (await remote.FindAsync(created, CancellationToken.None))!.Marker);

        await remote.ArchiveAsync(created, CancellationToken.None);
        Assert.Null(await remote.FindAsync(created, CancellationToken.None));
        Assert.Null(await remote.FindAsync("missing", CancellationToken.None));
    }

    [Fact]
    public async Task A_status_column_counts_as_done_by_its_complete_group()
    {
        _notion.Database = FakeNotion.Schema(withStatus: true);
        _notion.AddPage("p1", new JsonObject
        {
            ["Name"] = new JsonObject { ["type"] = "title", ["title"] = new JsonArray(new JsonObject { ["plain_text"] = "Shipped" }) },
            ["Status"] = new JsonObject { ["type"] = "status", ["status"] = new JsonObject { ["name"] = "Done" } },
        });
        var remote = await NotionRemote.ConnectAsync(Api(), FakeNotion.Db, CancellationToken.None);

        Assert.True((await remote.ListAsync(CancellationToken.None)).Single().Fields.Done);
        Assert.Equal(ConnectorFields.Title | ConnectorFields.Done, remote.Fields);

        await remote.UpdateAsync("p1", new SyncedFields("Shipped", false, null, Priority.Normal, [], null), CancellationToken.None);
        Assert.Equal("Not started", _notion.Pages["p1"]["properties"]!["Status"]!["status"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Busy_answers_are_retried_and_errors_say_what_Notion_said()
    {
        _notion.TooManyRequests = 2;
        Assert.Single(await Api().DatabasesAsync(CancellationToken.None));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Api("wrong").DatabasesAsync(CancellationToken.None));
        Assert.Contains("API token is invalid", ex.Message, StringComparison.Ordinal);
    }
}
