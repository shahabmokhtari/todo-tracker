using System.Text.Json.Nodes;

namespace OneThing.Core.Tests.Fixtures;

/// <summary>
/// tests/fixtures/outline.json: the Tasks tree (Outline) — group rows, leaf counts, the Show choices, search, zoom and
/// its path, Open to, and titles without the parent's name — so the web app, Swift and Kotlin draw the same tree. The
/// board and each case's options are written here; the rows, path, zoom and sets are what this version of the core says.
/// </summary>
public sealed class OutlineFixtureTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Snoozed = T0.AddDays(1);

    private static readonly string[] GroupNames = ["Work", "Personal", "Errands"];

    /// <summary>The board: (key, title, group, parent, done, archived, sequential, snoozed).</summary>
    private static readonly (string Key, string Title, string? Group, string? Parent, bool Done, bool Archived, bool Sequential, bool Snoozed)[] Tasks =
    [
        ("launch", "Launch the site", "Work", null, false, false, false, false),
        ("copy", "Launch the site: Write the copy", null, "launch", false, false, false, false),
        ("hero", "Hero section", null, "copy", false, false, false, false),
        ("faq", "Launch the site - FAQ", null, "copy", false, false, false, false),
        ("domain", "launch the site – Buy the domain", null, "launch", true, false, false, false),
        ("ship", "Launch the site — Ship it", null, "launch", false, false, false, false),
        ("bare", "Launch the site:", null, "launch", false, false, false, false),
        ("rollout", "Roll out v2", "Work", null, false, false, true, false),
        ("ring0", "Ring 0", null, "rollout", false, false, false, false),
        ("ring1", "Ring 1", null, "rollout", false, false, false, false),
        ("ring2", "Ring 2", null, "rollout", false, false, false, false),
        ("report", "Quarterly report", "Work", null, true, false, false, false),
        ("call", "Call the vendor", "Work", null, false, false, false, true),
        ("trip", "Plan the trip", "Personal", null, false, false, false, false),
        ("flights", "Book flights", null, "trip", false, false, false, true),
        ("hotel", "Book hotel", null, "trip", true, false, false, false),
        ("packing", "Packing list", null, "trip", true, false, false, false),
        ("sunscreen", "Sunscreen", null, "packing", false, false, false, false),
        ("passport", "Renew passport", "Personal", null, false, false, false, false),
        ("old", "Old project", "Personal", null, true, true, false, false),
        ("oldstep", "Old step", null, "old", false, false, false, false),
    ];

    private static readonly (string Title, string? Parent)[] Titles =
    [
        ("Publisher accounts: Pick the contact phone number", "Publisher accounts"),
        ("Launch - Buy the domain", "Launch"),
        ("Launch – Buy the domain", "Launch"),
        ("Launch — Buy the domain", "Launch"),
        ("LAUNCH:   shout", "launch"),
        ("Écrire: le texte", "écrire"),
        ("Launch:", "Launch"),
        ("Launch:   ", "Launch"),
        ("Launch —", "Launch"),
        ("Launcher: other", "Launch"),
        ("Launch-pad", "Launch"),
        ("Launch—no space", "Launch"),
        ("The launch: later", "Launch"),
        ("Launch: copy", null),
        ("Launch: copy", ""),
    ];

    private sealed record Case(string Name, string? Group = null, string? Zoom = null, string Show = "todo", string? Query = null, string[]? Expanded = null, string[]? ClosedGroups = null, bool OpenTo = false);

    private static IEnumerable<Case> Cases()
    {
        yield return new("All groups: one row per group in the board's order with its top-level tasks under it, closed; finished tasks hidden; an empty group still shows");
        yield return new("A closed group shows only its row", ClosedGroups: ["Work"]);
        yield return new("Open rows show their subtasks; parents count the leaves under them at every depth; a group adds up its top-level tasks", Expanded: ["launch", "copy"]);
        yield return new("One group picked: no group rows, its top-level tasks start the tree", Group: "Work", Expanded: ["rollout"]);
        yield return new("Everything: finished tasks too, archived ones never", Show: "all", Expanded: ["launch", "trip", "packing"]);
        yield return new("Can do now: what can be done now, with its parents; groups with nothing to show are left out", Show: "now", Expanded: ["launch", "copy", "rollout", "trip"]);
        yield return new("Waiting: snoozed tasks and a sequential task's later steps, with their parents", Show: "waiting", Expanded: ["rollout", "trip"]);
        yield return new("A choice narrows the tree but doesn't open it: a parent kept for its subtasks stays closed", Show: "waiting");
        yield return new("Search: what matches, with its parents and subtasks, shown open on the way to it; other groups left out", Query: "hero");
        yield return new("Search opens a closed group too; a finished match stays hidden under To do", Query: "book", ClosedGroups: ["Personal"]);
        yield return new("Search with Everything shows the finished match", Query: "book", Show: "all");
        yield return new("Search that finds nothing: no rows", Query: "zebra");
        yield return new("Zoom: the zoomed task's subtasks start the tree; the parent's name is left out of their titles; New task adds inside it; its top level can't move out", Zoom: "launch", Expanded: ["copy"]);
        yield return new("Zoom deeper: the path lists every parent; a grandparent's name stays in a title", Zoom: "copy");
        yield return new("Zoom with a group picked and a Show choice", Group: "Work", Zoom: "rollout", Show: "waiting");
        yield return new("Zoom on a finished task: out to its nearest parent that's shown", Zoom: "packing");
        yield return new("Zoom on a finished task with Everything: it stays", Zoom: "packing", Show: "all");
        yield return new("Zoom on a task without subtasks: its parent", Zoom: "hero");
        yield return new("Zoom on a task in another group than the one picked: the top", Group: "Personal", Zoom: "launch");
        yield return new("Zoom on an archived task: the top", Zoom: "old", Show: "all");
        yield return new("Zoom on a task that's gone: the top", Zoom: "gone");
        yield return new("Zoom on a task the Show choice hides, with no parent shown: the top", Zoom: "trip", Show: "now");
        yield return new("Zoom with a search that leaves it out: the top", Zoom: "trip", Query: "hero");
        yield return new("Zoom with a search that keeps it: it stays, shown open on the way", Zoom: "launch", Query: "hero");
        yield return new("Open to under All groups: each level sets exactly how deep the tree is open", Expanded: ["trip", "copy"], ClosedGroups: ["Errands"], OpenTo: true);
        yield return new("Open to with a group picked: only that group's tasks change; group rows aren't there (Groups is the same as Tasks)", Group: "Work", Expanded: ["trip", "copy"], ClosedGroups: ["Errands"], OpenTo: true);
        yield return new("Open to while zoomed: only what's under the zoom changes", Zoom: "launch", Expanded: ["trip", "launch", "hero"], OpenTo: true);
    }

    [Fact]
    public void The_outline_fixture_is_how_this_version_draws_the_tasks_tree()
    {
        var (board, ids) = Build();
        var keys = ids.ToDictionary(kv => kv.Value, kv => kv.Key);
        var groupIds = board.Groups.ToDictionary(g => g.Name, g => g.Id);
        var groupNames = board.Groups.ToDictionary(g => g.Id, g => g.Name);
        string Key(Guid id) => keys.TryGetValue(id, out var key) ? key : groupNames[id];
        JsonArray Sorted(IEnumerable<Guid> set, bool groups) =>
            [.. (groups ? board.Groups.Select(g => g.Id) : ids.Values).Where(set.Contains).Select(id => (JsonNode)Key(id))];

        JsonArray RowsOf(OutlineOptions options) => [.. Outline.Rows(board, options, T0).Select(r => (JsonNode)new JsonObject
        {
            ["kind"] = r.Kind == OutlineRowKind.Group ? "group" : "task",
            ["id"] = Key(r.Id),
            ["depth"] = r.Depth,
            ["title"] = r.Title,
            ["fullTitle"] = r.FullTitle,
            ["hasChildren"] = r.HasChildren,
            ["expanded"] = r.Expanded,
            ["done"] = r.Done,
            ["total"] = r.Total,
            ["left"] = r.Left,
            ["state"] = r.State?.ToString().ToLowerInvariant(),
            ["outdent"] = r.CanOutdent,
        })];

        var cases = new JsonArray();
        foreach (var c in Cases())
        {
            var options = new OutlineOptions
            {
                GroupId = c.Group is { } g ? groupIds[g] : null,
                ZoomId = c.Zoom is { } z ? ids.GetValueOrDefault(z, Guid.NewGuid()) : null,
                Show = Outline.ParseShow(c.Show)!.Value,
                Query = c.Query,
                Expanded = (c.Expanded ?? []).Select(k => ids[k]).ToHashSet(),
                ClosedGroups = (c.ClosedGroups ?? []).Select(n => groupIds[n]).ToHashSet(),
            };
            var written = new JsonObject
            {
                ["name"] = c.Name,
                ["options"] = new JsonObject
                {
                    ["group"] = c.Group,
                    ["zoom"] = c.Zoom,
                    ["show"] = c.Show,
                    ["query"] = c.Query,
                    ["expanded"] = new JsonArray([.. (c.Expanded ?? []).Select(k => (JsonNode)k)]),
                    ["closedGroups"] = new JsonArray([.. (c.ClosedGroups ?? []).Select(n => (JsonNode)n)]),
                },
            };
            if (c.Query is { } q)
            {
                written["keep"] = Sorted(SearchKeeps(board, q), groups: false);
            }

            var zoom = Outline.ResolveZoom(board, options, T0);
            written["zoom"] = zoom is { } zid ? Key(zid) : null;
            written["rows"] = RowsOf(options);
            written["path"] = new JsonArray([.. Outline.Path(board, options, T0).Select(p => (JsonNode)new JsonObject
            {
                ["kind"] = p.Kind.ToString().ToLowerInvariant(),
                ["id"] = p.Id is { } pid ? Key(pid) : null,
                ["title"] = p.Title,
                ["done"] = p.Done,
                ["total"] = p.Total,
            })]);
            var (parent, group) = Outline.NewTaskPlace(board, options, T0);
            written["newTask"] = new JsonObject { ["parent"] = parent is { } p ? Key(p) : null, ["group"] = Key(group) };
            if (c.OpenTo)
            {
                var levels = new JsonObject();
                foreach (var level in Enum.GetValues<OutlineLevel>())
                {
                    var open = Outline.OpenTo(board, options, level, T0);
                    levels[level.ToString().ToLowerInvariant()] = new JsonObject
                    {
                        ["expanded"] = Sorted(open.Expanded, groups: false),
                        ["closedGroups"] = Sorted(open.ClosedGroups, groups: true),
                        ["rows"] = RowsOf(options with { Expanded = open.Expanded, ClosedGroups = open.ClosedGroups }),
                    };
                }

                written["openTo"] = levels;
            }

            cases.Add(written);
        }

        var nodes = new JsonObject();
        foreach (var item in board.AllItems())
        {
            var leaves = item.SelfAndDescendants().Skip(1).Where(i => i.Children.Count == 0).ToList();
            nodes[keys[item.Id]] = new JsonObject
            {
                ["state"] = Agenda.StateOf(item, T0).ToString().ToLowerInvariant(),
                ["doneCount"] = leaves.Count(l => l.IsDone),
                ["totalCount"] = leaves.Count,
            };
        }

        FixtureFile.Check("outline.json", new JsonObject
        {
            ["about"] = "The Tasks tree (Outline): which rows show and how (group rows under All groups, leaf counts, the Show choices, search, zoom and its path, Open to, titles without the parent's name). Only how the tree is shown; nothing in the tasks changes. Generated by the C# core (OutlineFixtureTests, Outline.cs); the web app (tests/web/outline.test.mjs, outlinemodel.js), Swift and Kotlin run it.",
            ["shape"] = new JsonObject
            {
                ["now"] = "the time every case is worked out at (states depend on it: snoozed tasks wait until the day after)",
                ["groups"] = "the board's groups, in order (a new board's Work and Personal, then Errands, added; Errands has no tasks)",
                ["tasks"] = "{ key, title, group?: name (top-level tasks), parent?: key, sequential?: true, done?: true, archived?: true, snoozed?: true }, added in this order at `now`; then, in order, done ones are completed (a parent's subtasks with it), snoozed ones deferred until `snoozedUntil`, archived ones archived",
                ["snoozedUntil"] = "when snoozed tasks come back",
                ["nodes"] = "what /api/tree says about each task at `now` (state, doneCount, totalCount): for a port that reads the tree from the server (the web app) instead of working it out",
                ["titles"] = "{ title, parent, shown }: shownTitle(title, parentTitle). When the title starts with the parent's title (any case) followed by ':', ' -', ' –' or ' —', the rest with leading spaces dropped, unless that's empty",
                ["cases[].options"] = "{ group: name|null (null: all groups), zoom: key|null (a key that isn't a task: a task that's gone), show: todo|now|waiting|all, query: search text|null, expanded: [keys] (open tasks), closedGroups: [names] }",
                ["cases[].keep"] = "with a query: the tasks the search keeps (matches, their parents and their subtasks; what /api/tree?q= returns), in the order of `tasks`",
                ["cases[].zoom"] = "resolveZoom: the zoom used (key) or null (the top). The asked task when it's shown and has subtasks, else its nearest parent that is; null for one that's gone, archived or in another group than the one picked",
                ["cases[].rows"] = "rows(board, options, now), in order: { kind: group|task, id: key or group name, depth, title (as shown), fullTitle, hasChildren (rows under it are shown when open), expanded, done, total (leaves under it; 0 for a task without subtasks; a group adds up its top-level tasks, a task without subtasks counting as one), left (total - done), state (actionable|waiting|locked|container|done; null for groups), outdent (it can move out one level: not top level, not at the top of a zoom) }",
                ["cases[].path"] = "path(board, options): [] unless zoomed; else { kind: all|group|task, id, title, done, total }: All groups, the group, each parent, the zoomed task (a task part's title as shown under the part before it)",
                ["cases[].newTask"] = "where New task adds: { parent: the zoom (key) or null, group: the zoom's group, else the group picked, else the first group }",
                ["cases[].openTo"] = "openTo(board, options, level) for each level (groups, tasks, subtasks, all): the sets afterwards (order doesn't matter; listed in the order of tasks and groups) and the rows they give",
            },
            ["now"] = T0.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            ["snoozedUntil"] = Snoozed.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            ["groups"] = new JsonArray([.. GroupNames.Select(n => (JsonNode)n)]),
            ["tasks"] = new JsonArray([.. Tasks.Select(t =>
            {
                var o = new JsonObject { ["key"] = t.Key, ["title"] = t.Title };
                if (t.Group is not null) o["group"] = t.Group;
                if (t.Parent is not null) o["parent"] = t.Parent;
                if (t.Sequential) o["sequential"] = true;
                if (t.Done) o["done"] = true;
                if (t.Archived) o["archived"] = true;
                if (t.Snoozed) o["snoozed"] = true;
                return (JsonNode)o;
            })]),
            ["nodes"] = nodes,
            ["titles"] = new JsonArray([.. Titles.Select(t => (JsonNode)new JsonObject { ["title"] = t.Title, ["parent"] = t.Parent, ["shown"] = Outline.ShownTitle(t.Title, t.Parent) })]),
            ["cases"] = cases,
        });
    }

    private static (TaskBoard Board, Dictionary<string, Guid> Ids) Build()
    {
        var board = new TaskBoard();
        foreach (var name in GroupNames.Skip(board.Groups.Count))
        {
            board.AddGroup(name, null, Actor.User, T0);
        }

        Assert.Equal(GroupNames, board.Groups.Select(g => g.Name));
        var ids = new Dictionary<string, Guid>();
        foreach (var t in Tasks)
        {
            ids[t.Key] = board.AddTask(
                new NewTask(t.Title)
                {
                    ParentId = t.Parent is { } p ? ids[p] : null,
                    GroupId = t.Group is { } g ? board.Groups.Single(x => x.Name == g).Id : null,
                    Sequential = t.Sequential,
                },
                Actor.User,
                T0).Id;
        }

        foreach (var t in Tasks.Where(t => t.Done))
        {
            board.Complete(ids[t.Key], Actor.User, T0);
        }

        foreach (var t in Tasks.Where(t => t.Snoozed))
        {
            board.ScheduleNextAction(ids[t.Key], Snoozed, Actor.User, T0);
        }

        foreach (var t in Tasks.Where(t => t.Archived))
        {
            board.Archive(ids[t.Key], Actor.User, T0);
        }

        return (board, ids);
    }

    private static HashSet<Guid> SearchKeeps(TaskBoard board, string query)
    {
        var keep = new HashSet<Guid>();
        foreach (var match in TaskQuery.Parse(query).Apply(board))
        {
            keep.UnionWith(match.SelfAndDescendants().Select(i => i.Id));
            keep.UnionWith(match.Ancestors().Select(i => i.Id));
        }

        return keep;
    }
}
