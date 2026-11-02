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

    private async Task<ConnectorRun> RunAsync(bool adopt = false, bool groupOnly = false)
    {
        var run = await _runner.RunAsync(_remote, new ConnectorTarget(_group, ConnectorDirection.Both, adopt, groupOnly), _links, CancellationToken.None);
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
    public async Task A_label_that_differs_only_in_case_from_the_task_never_ping_pongs()
    {
        // The repository already has "color:#FF8800" and "stage:Doing": GitHub gives those to an issue asking for
        // "color:#ff8800" and "stage:doing" (labels have no case there).
        _github.Label(_github.Repo, "color:#FF8800");
        _github.Label(_github.Repo, "stage:Doing");
        await _store.UpdateAsync(b => { var t = b.AddTask(new NewTask("Paint") { GroupId = _group, Color = "#ff8800" }, Actor.User, T0); b.SetStage(t.Id, Stage.Doing, Actor.User, T0); return t; });

        await RunAsync();
        Assert.Contains("color:#FF8800", _github.LabelsOf(1));
        foreach (var _ in Enumerable.Range(0, 3))
        {
            Assert.Empty((await RunAsync()).Plan.Ops);
        }
    }

    [Fact]
    public async Task Labels_this_app_cant_take_are_left_on_the_issue_and_never_hold_up_the_task()
    {
        _github.Add("Triage", null, false, "good first issue", "priority:p1", "stage:review", "color:red", "plan:Q3", "bug");
        await RunAsync();
        var id = ConnectorPlan.ImportId("github:me/todo-work", "1");
        var task = await _store.ReadAsync(b => b.Get(id));
        Assert.Equal(["bug", "good first issue"], task.Tags.Order(StringComparer.Ordinal));
        Assert.Equal((Priority.Normal, null), (task.Priority, task.Color));

        // Changed here: the issue's own labels stay as they were.
        await _store.UpdateAsync(b => { b.Update(id, new TaskChanges { Title = "Triage the inbox", Priority = Priority.High }, Actor.User, T0); return 0; });
        await RunAsync();
        Assert.Equal("Triage the inbox", _github.Issues[1]["title"]!.GetValue<string>());
        Assert.Equal(["bug", "color:red", "good first issue", "plan:Q3", "priority:high", "priority:p1", "stage:review"], _github.LabelsOf(1).Order(StringComparer.Ordinal));
        Assert.Empty((await RunAsync()).Plan.Ops);
    }

    [Fact]
    public async Task A_task_moved_to_another_group_leaves_this_groups_repository()
    {
        var task = await _store.UpdateAsync(b => b.AddTask(new NewTask("Book flights") { GroupId = _group }, Actor.User, T0));
        await RunAsync(groupOnly: true);
        var other = await _store.UpdateAsync(b => b.AddGroup("Travel", null, Actor.User, T0).Id);

        await _store.UpdateAsync(b => { b.MoveToGroup(task.Id, other, Actor.User, T0); return 0; });
        await RunAsync(groupOnly: true);

        Assert.Equal(("closed", "not_planned"), (_github.Issues[1]["state"]!.GetValue<string>(), _github.Issues[1]["state_reason"]!.GetValue<string>()));
        Assert.False((await _store.ReadAsync(b => b.Get(task.Id))).IsDone);
    }

    [Fact]
    public async Task Long_titles_fit_and_old_time_labels_are_tidied_away()
    {
        var task = await _store.UpdateAsync(b => b.AddTask(new NewTask(new string('t', 280)) { GroupId = _group }, Actor.User, T0));
        await _store.UpdateAsync(b => { b.ScheduleNextAction(task.Id, T0.AddDays(1), Actor.User, T0); return 0; });
        await RunAsync();
        Assert.Equal(GitHubRemote.MaxTitle, _github.Issues[1]["title"]!.GetValue<string>().Length);
        Assert.Empty((await RunAsync()).Plan.Ops);

        await _store.UpdateAsync(b => { b.ScheduleNextAction(task.Id, T0.AddDays(2), Actor.User, T0); return 0; });
        await RunAsync();
        await _remote.PruneLabelsAsync(CancellationToken.None);

        Assert.Equal(["snooze:2026-01-07T09:00Z"], _github.LabelsByRepo[_github.Repo]);
    }

    [Fact]
    public void An_issue_closed_as_not_planned_is_never_brought_back_on_another_computer()
    {
        var input = new ConnectorPlanInput("github:me/x", [], [new RemoteItem("7", new SyncedFields("Deleted elsewhere", true, null, Priority.Normal, [], null), Guid.NewGuid(), Dropped: true)], [], ConnectorDirection.Both, (ConnectorFields)127) { AdoptMarked = true };

        Assert.Empty(ConnectorPlan.Make(input).Ops);
    }

    [Fact]
    public void Reminders_and_labels_merge_one_by_one_when_both_sides_changed()
    {
        var id = Guid.NewGuid();
        SyncedFields F(string remind, string labels) => new("Task", false, null, Priority.Normal, [], null) { Extras = new Dictionary<string, string> { [ExtraKeys.Remind] = remind, [ExtraKeys.Labels] = labels } };
        // Here: reminder A went off and label X was added. There: reminder B and label Y were added.
        var plan = ConnectorPlan.Make(new ConnectorPlanInput(
            "github:me/x",
            [new LocalItem(id, F(string.Empty, "Deep work\nX"), true, false)],
            [new RemoteItem("1", F("2026-01-06T09:00Z,2026-01-08T09:00Z", "Deep work\nY"), id)],
            [new ConnectorLink(id, "1", F("2026-01-06T09:00Z", "Deep work"))],
            ConnectorDirection.Both,
            (ConnectorFields)127));

        var merged = Assert.Single(plan.Links).Snapshot!;
        Assert.Equal(("2026-01-08T09:00Z", "Deep work\nX\nY"), (merged.Extra(ExtraKeys.Remind), merged.Extra(ExtraKeys.Labels)));
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

    private GitHubRemote Scoped(string label) => new(_http, _ => Task.FromResult("gho_test"), _github.Repo, label);

    [Fact]
    public async Task A_group_in_a_shared_repository_labels_every_issue_it_makes_and_sees_only_its_own()
    {
        var work = Scoped("group:Work");
        _github.Add("Someone else's", null, false, "group:Personal");
        _github.Add("Not labelled");

        var id = await work.CreateAsync(Guid.NewGuid(), new SyncedFields("Ship it", false, null, Priority.High, ["release"], null), CancellationToken.None);

        Assert.Equal(["group:Work", "priority:high", "release"], _github.LabelsOf(int.Parse(id, System.Globalization.CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal));
        var listed = Assert.Single(await work.ListAsync(CancellationToken.None));
        Assert.Equal(id, listed.Id);
        Assert.Equal(["release"], listed.Fields.Tags);
        Assert.Null(await work.FindAsync("1", CancellationToken.None));
        Assert.NotNull(await work.FindAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task The_group_label_is_never_a_tag_and_stays_on_the_issue_when_its_labels_change()
    {
        var work = Scoped("group:Work");
        _github.Add("Plan the offsite", null, false, "group:Work", "travel");
        var before = Assert.Single(await work.ListAsync(CancellationToken.None)).Fields;
        Assert.Equal(["travel"], before.Tags);

        await work.UpdateAsync("1", before with { Tags = ["travel", "team"], Priority = Priority.High }, before, CancellationToken.None);
        Assert.Equal(["group:Work", "priority:high", "team", "travel"], _github.LabelsOf(1).Order(StringComparer.Ordinal));

        // Taken off between the list and the change: it goes back on (unless the issue is in another group now).
        _github.Issues[1]["labels"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["name"] = "team" });
        await work.UpdateAsync("1", before, before with { Tags = ["team"] }, CancellationToken.None);
        Assert.Contains("group:Work", _github.LabelsOf(1));
        _github.Issues[1]["labels"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["name"] = "group:Personal" });
        await work.UpdateAsync("1", before with { Tags = ["x"] }, before, CancellationToken.None);
        Assert.Equal(["group:Personal", "x"], _github.LabelsOf(1).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Tidying_labels_keeps_group_labels_and_time_labels_another_groups_issues_still_use()
    {
        _github.Add("Personal errand", null, false, "group:Personal", "snooze:2026-01-07T09:00Z");
        _github.Add("Old work", null, false, "group:Work", "snooze:2026-01-08T09:00Z");
        _github.Issues[2]["labels"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["name"] = "group:Work" });

        await Scoped("group:Work").PruneLabelsAsync(CancellationToken.None);

        Assert.Equal(["group:Personal", "group:Work", "snooze:2026-01-07T09:00Z"], _github.LabelsByRepo[_github.Repo].Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_group_takes_the_issues_no_group_has_yet_and_leaves_other_groups_issues_alone()
    {
        _github.Add("Made before labels");
        _github.Add("Personal", null, false, "group:Personal");
        _github.Add("A pull request")["pull_request"] = new System.Text.Json.Nodes.JsonObject();

        await Scoped("group:Work").ClaimUnlabelledAsync(CancellationToken.None);

        Assert.Equal(["group:Work"], _github.LabelsOf(1));
        Assert.Equal(["group:Personal"], _github.LabelsOf(2));
        Assert.Empty(_github.LabelsOf(3));
    }

    [Fact]
    public async Task An_issue_moved_to_another_group_on_GitHub_takes_its_task_there_and_nothing_is_lost()
    {
        var personal = await _store.UpdateAsync(b => b.AddGroup("Home", null, Actor.User, T0).Id);
        var work = Scoped("group:Work");
        var home = Scoped("group:Home");
        var task = await _store.UpdateAsync(b => b.AddTask(new NewTask("Fix the bike") { GroupId = _group }, Actor.User, T0));
        var target = new ConnectorTarget(_group, ConnectorDirection.Both, GroupOnly: true, TakeMoved: true);
        var homeTarget = target with { GroupId = personal };
        var run = await _runner.RunAsync(work, target, [], CancellationToken.None);
        IReadOnlyList<ConnectorLink> homeLinks = [];

        // Relabelled on GitHub: group:Work → group:Home.
        _github.Issues[1]["labels"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["name"] = "group:Home" });
        run = await _runner.RunAsync(work, target, run.Links, CancellationToken.None);
        Assert.Equal(LinkState.RemoteGone, Assert.Single(run.Links).State);
        Assert.Equal(_group, (await _store.ReadAsync(b => b.Get(task.Id))).GroupId); // gone from this group: the task stays

        foreach (var _ in Enumerable.Range(0, 2))
        {
            var homeRun = await _runner.RunAsync(home, homeTarget, homeLinks, CancellationToken.None);
            Assert.Empty(homeRun.Problems);
            homeLinks = homeRun.Links;
            run = await _runner.RunAsync(work, target, run.Links, CancellationToken.None);
        }

        Assert.Equal(personal, (await _store.ReadAsync(b => b.Get(task.Id))).GroupId);
        Assert.Equal("open", _github.Issues[1]["state"]!.GetValue<string>());
        Assert.Single(_github.Issues);
        Assert.Empty(run.Links);
    }

    [Theory]
    [InlineData("Work", "group:Work")]
    [InlineData("  Side, projects ", "group:Side projects")]
    [InlineData("A group with a name much longer than a label can hold", "group:A group with a name much longer than a label")]
    public void A_groups_label_is_its_name_and_fits_a_GitHub_label(string group, string label) => Assert.Equal(label, GitHubRemote.GroupLabel(group));

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
