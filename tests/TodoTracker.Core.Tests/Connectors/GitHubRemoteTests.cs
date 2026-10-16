using Microsoft.Extensions.Time.Testing;
using TodoTracker.Core.Connectors;
using TodoTracker.Core.Tests.Connectors;

namespace TodoTracker.Core.Tests;

/// <summary>A group kept in step with a GitHub repository's issues: tasks and issues, both ways, with everything a task
/// has carried by prefixed labels.</summary>
public sealed class GitHubRemoteTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
    private readonly InMemoryBoardStore _store = new();
    private readonly FakeTimeProvider _time = new(T0);
    private readonly FakeGitHub _github = new();
    private readonly HttpClient _http;
    private readonly GitHubRemote _remote;
    private readonly ConnectorRunner _runner;
    private IReadOnlyList<ConnectorLink> _links = [];
    private readonly Guid _group;

    public GitHubRemoteTests()
    {
        _http = new HttpClient(_github) { BaseAddress = new Uri("https://api.github.com/") };
        _remote = new GitHubRemote(_http, _ => Task.FromResult("gho_test"), _github.Repo);
        _runner = new ConnectorRunner("github:me/todo-work", "GitHub", _store, _time, TimeZoneInfo.Utc);
        _group = _store.UpdateAsync(b => b.AddGroup("Synced", null, Actor.User, T0).Id).Result;
    }

    public void Dispose()
    {
        _store.Dispose();
        _http.Dispose();
        _github.Dispose();
    }

    private async Task<ConnectorRun> RunAsync(bool adopt = false)
    {
        var run = await _runner.RunAsync(_remote, new ConnectorTarget(_group, ConnectorDirection.Both, adopt), _links, CancellationToken.None);
        Assert.Empty(run.Problems);
        _links = run.Links;
        return run;
    }

    [Fact]
    public async Task A_task_goes_out_as_an_issue_with_its_state_in_prefixed_labels_and_comes_back_the_same()
    {
        var task = await _store.UpdateAsync(b =>
        {
            var t = b.AddTask(new NewTask("Renew passport") { GroupId = _group, Priority = Priority.High, Deadline = T0.AddDays(7), Tags = ["admin"], Labels = ["Deep work"], Details = "Bring photos", Icon = "🛂", Color = "#ff8800", Repeat = "every year" }, Actor.User, T0);
            b.ScheduleNextAction(t.Id, T0.AddDays(2), Actor.User, T0, notify: true);
            b.AddReminder(t.Id, T0.AddDays(1).AddHours(3), "Book the photo", Actor.User, T0);
            b.SetStage(t.Id, Stage.Doing, Actor.User, T0);
            b.Plan(t.Id, T0.AddDays(3), 45, Actor.User, T0);
            return t;
        });

        await RunAsync();

        var issue = Assert.Single(_github.Issues.Values);
        Assert.Equal("Renew passport", issue["title"]!.GetValue<string>());
        Assert.Equal($"Bring photos\n\n<!-- todotracker:id={task.Id:D} -->", issue["body"]!.GetValue<string>());
        Assert.Equal(
            ["admin", "color:#ff8800", "due:2026-01-12", "icon:🛂", "label:Deep work", "plan:2026-01-08T09:00Z/45", "priority:high", "remind:2026-01-06T12:00Z", "repeat:every year", "snooze:2026-01-07T09:00Z", "stage:doing"],
            _github.LabelsOf(1).Order(StringComparer.Ordinal));
        Assert.Empty((await RunAsync()).Plan.Ops);

        // Another computer (GitHub is where the tasks live): the same task, the same id, the same state.
        using var there = new InMemoryBoardStore();
        var group = await there.UpdateAsync(b => b.AddGroup("Synced", null, Actor.User, T0).Id);
        var run = await new ConnectorRunner("github:me/todo-work", "GitHub", there, _time, TimeZoneInfo.Utc).RunAsync(_remote, new ConnectorTarget(group, ConnectorDirection.Both, AdoptMarked: true), [], CancellationToken.None);
        Assert.Empty(run.Problems);
        var copy = await there.ReadAsync(b => b.Find(task.Id));
        Assert.NotNull(copy);
        Assert.Equal((Priority.High, "Bring photos", Stage.Doing, "🛂", "#ff8800", "every year"), (copy.Priority, copy.Details, copy.Stage, copy.Icon, copy.Color, copy.Repeat?.ToString()));
        Assert.Equal((T0.AddDays(2), T0.AddDays(3), 45), (copy.NextActionAt, copy.PlannedAt, copy.PlannedMinutes));
        Assert.Equal(["admin"], copy.Tags);
        Assert.Equal(["Deep work"], copy.Labels);
        Assert.Contains(copy.Reminders, r => r.Kind == ReminderKind.Manual && r.DueAt == T0.AddDays(1).AddHours(3));
    }

    [Fact]
    public async Task Labels_changed_on_GitHub_snooze_remind_and_move_the_task_here_and_closing_finishes_it()
    {
        _github.Add("Call the bank", "Ask about the fee", false, "money", "priority:critical");
        await RunAsync();
        var id = ConnectorPlan.ImportId("github:me/todo-work", "1");
        Assert.Equal((Priority.Critical, "Ask about the fee"), await _store.ReadAsync(b => (b.Get(id).Priority, b.Get(id).Details)));
        Assert.Contains($"todotracker:id={id:D}", _github.Issues[1]["body"]!.GetValue<string>(), StringComparison.Ordinal);

        _github.Issues[1]["labels"] = new System.Text.Json.Nodes.JsonArray(
            new System.Text.Json.Nodes.JsonObject { ["name"] = "money" },
            new System.Text.Json.Nodes.JsonObject { ["name"] = "snooze:2026-01-09T08:00Z" },
            new System.Text.Json.Nodes.JsonObject { ["name"] = "remind:2026-01-06T10:30Z" },
            new System.Text.Json.Nodes.JsonObject { ["name"] = "stage:doing" });
        await RunAsync();

        var item = await _store.ReadAsync(b => b.Get(id));
        Assert.Equal((Priority.Normal, T0.AddDays(4).AddHours(-1), Stage.Doing), (item.Priority, item.NextActionAt, item.Stage));
        Assert.Contains(item.Reminders, r => r.Kind == ReminderKind.Manual && r.DueAt == new DateTimeOffset(2026, 1, 6, 10, 30, 0, TimeSpan.Zero));

        // Its reminder label removed there: dismissed here. Closed there: done here.
        _github.Issues[1]["labels"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["name"] = "snooze:2026-01-09T08:00Z" }, new System.Text.Json.Nodes.JsonObject { ["name"] = "stage:doing" });
        _github.Issues[1]["state"] = "closed";
        await RunAsync();
        item = await _store.ReadAsync(b => b.Get(id));
        Assert.True(item.IsDone);
        Assert.DoesNotContain(item.Reminders, r => r.Kind == ReminderKind.Manual && r.DismissedAt is null);
        Assert.Empty(item.Tags);
    }

    [Fact]
    public async Task Deleting_a_task_closes_its_issue_as_not_planned_and_pull_requests_and_too_long_labels_are_left_alone()
    {
        _github.Add("A pull request")["pull_request"] = new System.Text.Json.Nodes.JsonObject();
        var task = await _store.UpdateAsync(b => b.AddTask(new NewTask("Tidy up") { GroupId = _group, Tags = ["short"], Repeat = "every 2 weeks on Monday, Tuesday, Wednesday, Thursday" }, Actor.User, T0));
        await RunAsync();

        Assert.Equal(2, _github.Issues.Count);
        Assert.Equal(["short"], _github.LabelsOf(2));
        Assert.Empty((await RunAsync()).Plan.Ops); // a repeat rule longer than a label can't go out: not a change every time

        await _store.UpdateAsync(b => b.Delete(task.Id, Actor.User, T0));
        await RunAsync();
        Assert.Equal(("closed", "not_planned"), (_github.Issues[2]["state"]!.GetValue<string>(), _github.Issues[2]["state_reason"]!.GetValue<string>()));
        Assert.Equal(2, _github.Issues.Count);
    }

    [Fact]
    public async Task Every_page_of_issues_is_read()
    {
        foreach (var n in Enumerable.Range(1, 150))
        {
            _github.Add($"Issue {n}");
        }

        Assert.Equal(150, (await _remote.ListAsync(CancellationToken.None)).Count);
        Assert.Equal(2, _github.Requests.Count);
    }

    [Fact]
    public void Extras_merge_key_by_key_so_a_snooze_here_and_a_reminder_there_both_stay()
    {
        var id = Guid.NewGuid();
        var all = (ConnectorFields)127;
        SyncedFields F(params (string Key, string Value)[] extras) => new("Task", false, null, Priority.Normal, [], null) { Extras = extras.ToDictionary(e => e.Key, e => e.Value) };
        var before = F();
        var plan = ConnectorPlan.Make(new ConnectorPlanInput(
            "github:me/x",
            [new LocalItem(id, F((ExtraKeys.Snooze, "2026-01-07T09:00Z")), true, false)],
            [new RemoteItem("1", F((ExtraKeys.Remind, "2026-01-06T10:30Z")), id)],
            [new ConnectorLink(id, "1", before)],
            ConnectorDirection.Both,
            all));

        var merged = Assert.Single(plan.Links).Snapshot!;
        Assert.Equal(("2026-01-07T09:00Z", "2026-01-06T10:30Z"), (merged.Extra(ExtraKeys.Snooze), merged.Extra(ExtraKeys.Remind)));
        Assert.Contains(plan.Ops, op => op is UpdateLocal);
        Assert.Contains(plan.Ops, op => op is UpdateRemote);
    }

    [Fact]
    public void An_issue_made_on_another_computer_is_brought_in_with_its_own_id_only_where_GitHub_is_the_main_place()
    {
        var elsewhere = Guid.NewGuid();
        var input = new ConnectorPlanInput("github:me/x", [], [new RemoteItem("7", new SyncedFields("Made elsewhere", true, null, Priority.Normal, [], null), elsewhere)], [], ConnectorDirection.Both, (ConnectorFields)127);

        Assert.Empty(ConnectorPlan.Make(input).Ops);
        var adopted = Assert.IsType<CreateLocal>(Assert.Single(ConnectorPlan.Make(input with { AdoptMarked = true }).Ops));
        Assert.Equal(elsewhere, adopted.Id);
    }

    [Theory]
    [InlineData("me/todo-work", true)]
    [InlineData("my-org/tasks.personal", true)]
    [InlineData("../etc/passwd", false)]
    [InlineData("me/todo work", false)]
    [InlineData("me", false)]
    public void Only_owner_slash_name_is_a_repository(string repo, bool valid)
    {
        if (valid)
        {
            Assert.Equal(repo, GitHubRemote.ValidRepo(repo));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => GitHubRemote.ValidRepo(repo));
        }
    }
}
