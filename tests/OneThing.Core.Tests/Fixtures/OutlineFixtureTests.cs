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
        ("headline", "Write the copy: Headline", null, "copy", false, false, false, false),
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

    private static readonly (string Title, string? Parent, string? ParentShown)[] Titles =
    [
        ("Publisher accounts: Pick the phone number", "Go to market: Publisher accounts", "Publisher accounts"),
        ("Go to market: Publisher accounts - Pay the fee", "Go to market: Publisher accounts", "Publisher accounts"),
        ("Go to market: Ads", "Go to market: Publisher accounts", "Publisher accounts"),
        ("Publisher accounts: Pick the contact phone number", "Publisher accounts", null),
        ("Launch - Buy the domain", "Launch", null),
        ("Launch – Buy the domain", "Launch", null),
        ("Launch — Buy the domain", "Launch", null),
        ("LAUNCH:   shout", "launch", null),
        ("Écrire: le texte", "écrire", null),
        ("Launch:", "Launch", null),
        ("Launch:   ", "Launch", null),
        ("Launch —", "Launch", null),
        ("Launcher: other", "Launch", null),
        ("Launch-pad", "Launch", null),
        ("Launch—no space", "Launch", null),
        ("The launch: later", "Launch", null),
        ("Launch: copy", null, null),
        ("Launch: copy", "", null),
        // One comparison everywhere (C#'s OrdinalIgnoreCase): each UTF-16 unit upper-cased on its own (a unit whose upper
        // case is longer stays as it is), no normalization, no locale.
        ("İstanbul: the trip", "istanbul", null),
        ("istanbul: the trip", "İstanbul", null),
        ("\u00e9crire: le texte", "e\u0301crire", null),
        ("e\u0301crire: le texte", "\u00e9crire", null),
        ("\u212Aelvin: the scale", "kelvin", null),
        ("kelvin: the scale", "\u212Aelvin", null),
        ("Straße: the map", "STRASSE", null),
        ("STRASSE: the map", "Straße", null),
        ("ıstanbul: the trip", "ISTANBUL", null),
        ("ſtar: the map", "STAR", null),
        ("\u1F80: the letter", "\u1F88", null),
        ("\U00010428: the letter", "\U00010400", null),
        // Leading spaces as C#'s char.IsWhiteSpace has them: U+0085 is one, U+FEFF and U+001F aren't.
        ("Launch:\u0085copy", "Launch", null),
        ("Launch:\ufeffcopy", "Launch", null),
        ("Launch:\u001fcopy", "Launch", null),
        ("Launch:\u00a0\u3000copy", "Launch", null),
    ];

    /// <summary>A row's tags and its parent's (null: a top-level task), for <see cref="Outline.ShownTags"/>.</summary>
    private static readonly (string[] Tags, string[]? Parent)[] Tags =
    [
        (["publisher-accounts", "ios"], ["publisher-accounts"]),
        (["publisher-accounts"], ["publisher-accounts", "launch"]),
        (["publisher-accounts", "ios"], null),
        (["ios", "android"], []),
        ([], ["publisher-accounts"]),
        (["a", "b", "c"], ["b"]),
        (["c", "a", "b"], ["a", "x"]),
        (["Urgent", "home"], ["urgent"]),
        (["urgent"], ["URGENT"]),
        (["urg"], ["urgent"]),
        (["urgent"], ["urg"]),
        // As titles are compared (C#'s OrdinalIgnoreCase): no normalization, no locale.
        (["İstanbul"], ["istanbul"]),
        (["ıstanbul"], ["ISTANBUL"]),
        (["straße"], ["STRASSE"]),
        (["\u212Aelvin"], ["kelvin"]),
        (["\u00e9crire"], ["e\u0301crire"]),
        (["\u00e9crire"], ["\u00c9CRIRE"]),
        (["\U00010428"], ["\U00010400"]),
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
        yield return new("Zoom on a finished task: it stays, with nothing To do shows under it", Zoom: "packing");
        yield return new("Zoom on a finished task with Everything: it stays", Zoom: "packing", Show: "all");
        yield return new("Zoom on a task without subtasks: its parent", Zoom: "hero");
        yield return new("Zoom on a task in another group than the one picked: the top", Group: "Personal", Zoom: "launch");
        yield return new("Zoom on an archived task: the top", Zoom: "old", Show: "all");
        yield return new("Zoom on a task that's gone: the top", Zoom: "gone");
        yield return new("Zoom on a branch where nothing can be done now: Can do now keeps the zoom and shows nothing under it", Zoom: "trip", Show: "now");
        yield return new("Zoom on a branch where nothing waits: Waiting keeps the zoom and shows nothing under it", Zoom: "copy", Show: "waiting");
        yield return new("Zoom with a search that leaves it out: the top for now; the zoom is kept for when it's cleared", Zoom: "trip", Query: "hero");
        yield return new("Zoom with a search that keeps only its parent: the parent for now; the zoom is kept", Zoom: "packing", Query: "flights");
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
            written["remembered"] = Outline.RememberedZoom(board, options) is { } rid ? Key(rid) : null;
            written["zoomUp"] = Outline.ZoomUp(board, options, T0) is { } uid ? Key(uid) : null;
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
                ["titles"] = "{ title, parent, parentShown, shown }: shownTitle(title, parentTitle, parentShownTitle). When the title starts with the parent's title (any case) followed by ':', ' -', ' –' or ' —', the rest with leading spaces dropped, unless that's empty; when that leaves it as it was and the parent is shown shorter (parentShown, null: not), the same against that. In rows, a task's parentShown is its parent's row title (at the top of a zoom: the zoomed task's title in the path)",
                ["tags"] = "{ tags, parent, shown }: shownTags(tags, parentTags). A row shows only the tags its parent task doesn't also have (compared as titles are: any case, no normalization), in the task's order; a top-level task (parent null) shows all of them. In rows, a task's parent is its parent task (at the top of a zoom: the zoomed task); only how the row is drawn, the task keeps every tag",
                ["cases[].options"] = "{ group: name|null (null: all groups), zoom: key|null (a key that isn't a task: a task that's gone), show: todo|now|waiting|all, query: search text|null, expanded: [keys] (open tasks), closedGroups: [names] }",
                ["cases[].keep"] = "with a query: the tasks the search keeps (matches, their parents and their subtasks; what /api/tree?q= returns), in the order of `tasks`",
                ["cases[].zoom"] = "resolveZoom: the zoom drawn (key) or null (the top): the remembered one, or while a search leaves it out its nearest parent the search keeps (for now). The Show choice never moves it (it only narrows what's under it)",
                ["cases[].remembered"] = "rememberedZoom: the zoom a device keeps (key) or null. The asked task unless it's really gone: deleted, archived or in another group than the one picked: null; without subtasks: its parent. A search or a Show choice doesn't change it",
                ["cases[].zoomUp"] = "zoomUp: where Back or Escape goes from the zoom drawn: its parent (the task part of the path before it), or null (out to the tree; also when not zoomed)",
                ["cases[].rows"] = "rows(board, options, now), in order: { kind: group|task, id: key or group name, depth, title (as shown), fullTitle, hasChildren (rows under it are shown when open), expanded, done, total (leaves under it; 0 for a task without subtasks; a group adds up its top-level tasks, a task without subtasks counting as one), left (total - done), state (actionable|waiting|locked|container|done; null for groups), outdent (it can move out one level: not top level, not at the top of a zoom) }",
                ["cases[].path"] = "path(board, options): [] unless zoomed; else { kind: all|group|task, id, title, done, total }: All groups, the group, each parent, the zoomed task (a task part's title as shown under the part before it)",
                ["cases[].newTask"] = "where New task adds: { parent: the zoom (key) or null, group: the zoom's group, else the group picked, else the first group }",
                ["cases[].openTo"] = "openTo(board, options, level) for each level (groups, tasks, subtasks, all): the sets afterwards (order doesn't matter; listed in the order of tasks and groups) and the rows they give",
                ["tree"] = "the board before a move: each group's name, then its top-level tasks (archived ones too) and their subtasks, two spaces per level (keys)",
                ["moves[].options"] = "as cases[].options, without expanded and closedGroups (the moves don't depend on what's open)",
                ["moves[].keep"] = "with a query: what the search keeps, as cases[].keep",
                ["moves[].move"] = "{ do: indent|outdent|up|down|drop|dropOnGroup|newTask, task, target?, zone?: before|inside|after, group? }. indent: under the sibling above it on screen, last; outdent: right after its parent (not at the top of a zoom: its row says outdent false); up/down: past the sibling above/below on screen. On screen: kept by the Show choice and the search; at the top level, only the task's own group's tasks. drop: before, inside (last) or after the target row (next to a task of another group: into that group); dropOnGroup: to the top of that group (nothing when it's already its first task); newTask: a task 'new' (title New) first at newTask's place",
                ["moves[].tree"] = "the board afterwards (as tree), or \"refused\" when the move can't be made or changes nothing",
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
            ["titles"] = new JsonArray([.. Titles.Select(t => (JsonNode)new JsonObject { ["title"] = t.Title, ["parent"] = t.Parent, ["parentShown"] = t.ParentShown, ["shown"] = Outline.ShownTitle(t.Title, t.Parent, t.ParentShown) })]),
            ["tags"] = new JsonArray([.. Tags.Select(t => (JsonNode)new JsonObject
            {
                ["tags"] = new JsonArray([.. t.Tags.Select(x => (JsonNode)x)]),
                ["parent"] = t.Parent is null ? null : new JsonArray([.. t.Parent.Select(x => (JsonNode)x)]),
                ["shown"] = new JsonArray([.. Outline.ShownTags(t.Tags, t.Parent).Select(x => (JsonNode)x)]),
            })]),
            ["cases"] = cases,
            ["tree"] = TreeOf(board, keys),
            ["moves"] = Moves(),
        });
    }

    /// <summary>A step of a move as the server makes it: Move, Reorder, ReorderAfter, or a new task first in a place.</summary>
    private sealed record Op(string Do, string Task, string? Parent = null, string? Group = null, int? Index = null, string? Before = null, string? After = null);

    private sealed record MoveCase(string Name, string Do, string? Task, Op[]? Plan, string? Target = null, string? Zone = null, string? OnGroup = null, string? Group = null, string? Zoom = null, string Show = "todo", string? Query = null);

    private static Op Move(string task, string? parent = null, string? group = null, int? index = null) => new("move", task, parent, group, index);

    private static Op Before(string task, string before) => new("reorder", task, Before: before);

    private static Op After(string task, string after) => new("after", task, After: after);

    private static Op AddFirst(string? parent = null, string? group = null) => new("add", "new", parent, group);

    /// <summary>
    /// Moves made in the tree, each on a fresh board: the gesture (what the ports plan from) and, written here, the server
    /// changes it comes to (null: refused). The ports plan the gesture with their own code and must end at the same tree.
    /// </summary>
    private static IEnumerable<MoveCase> MoveCases()
    {
        yield return new("Indent: under the task above on screen, last; a finished one To do hides is stepped over", "indent", "ship", [Move("ship", parent: "copy")]);
        yield return new("Indent with Everything: under the finished task above", "indent", "ship", [Move("ship", parent: "domain")], Show: "all");
        yield return new("Indent with a search: only what the search keeps is next to it", "indent", "ship", null, Query: "ship");
        yield return new("Indent a group's first task under All groups: refused (another group's task isn't next to it)", "indent", "trip", null);
        yield return new("Indent at the top level: under the task above in its own group", "indent", "passport", [Move("passport", parent: "trip")]);
        yield return new("Outdent: out one level, right after its parent", "outdent", "hero", [Move("hero", parent: "launch"), After("hero", "copy")]);
        yield return new("Outdent to the top level: in its group, right after its parent", "outdent", "copy", [Move("copy", group: "Work"), After("copy", "launch")]);
        yield return new("Outdent at the top of a zoom: refused (it would leave the view)", "outdent", "copy", null, Zoom: "launch");
        yield return new("Outdent deeper in a zoom: right after its parent", "outdent", "hero", [Move("hero", parent: "launch"), After("hero", "copy")], Zoom: "launch");
        yield return new("Outdent a top-level task: refused", "outdent", "launch", null);
        yield return new("Up: past the task above on screen; a finished one To do hides is stepped over", "up", "ship", [Before("ship", "copy")]);
        yield return new("Up from the top of its parent: refused", "up", "copy", null);
        yield return new("Down at the end of a group under All groups: refused (the next group's task isn't next to it)", "down", "call", null);
        yield return new("Up with Can do now: only what can be done now is next to it", "up", "passport", null, Show: "now");
        yield return new("Down with a group picked: past the next task", "down", "launch", [After("launch", "rollout")], Group: "Work");
        yield return new("Down with Everything: past the finished task below", "down", "rollout", [After("rollout", "report")], Show: "all");
        yield return new("Drop before a task of another group: into that group, before it", "drop", "passport", [Move("passport", group: "Work"), Before("passport", "launch")], Target: "launch", Zone: "before");
        yield return new("Drop after another task's subtask: under that task, after it", "drop", "ring0", [Move("ring0", parent: "copy"), After("ring0", "hero")], Target: "hero", Zone: "after");
        yield return new("Drop before a sibling: only reordered", "drop", "bare", [Before("bare", "copy")], Target: "copy", Zone: "before");
        yield return new("Drop inside a task: last under it", "drop", "passport", [Move("passport", parent: "rollout")], Target: "rollout", Zone: "inside");
        yield return new("Drop a task into its own subtask: refused", "drop", "launch", null, Target: "hero", Zone: "inside");
        yield return new("Drop on a group's row: to the top of that group", "dropOnGroup", "hero", [Move("hero", group: "Personal", index: 0)], OnGroup: "Personal");
        yield return new("Drop a subtask on its own group's row: to the top level, first", "dropOnGroup", "copy", [Move("copy", group: "Work", index: 0)], OnGroup: "Work");
        yield return new("Drop a group's other task on its own group's row: to the top", "dropOnGroup", "passport", [Move("passport", group: "Personal", index: 0)], OnGroup: "Personal");
        yield return new("Drop a group's first task on its own group's row: nothing to do", "dropOnGroup", "trip", null, OnGroup: "Personal");
        yield return new("New task under All groups: first in the first group", "newTask", null, [AddFirst(group: "Work")]);
        yield return new("New task with a search that hides the first group: first in the first group all the same", "newTask", null, [AddFirst(group: "Work")], Query: "passport");
        yield return new("New task while zoomed: first inside the zoomed task", "newTask", null, [AddFirst(parent: "copy")], Zoom: "copy");
        yield return new("New task with a group picked: first in it", "newTask", null, [AddFirst(group: "Personal")], Group: "Personal");
    }

    private static JsonArray Moves()
    {
        var moves = new JsonArray();
        foreach (var m in MoveCases())
        {
            var (board, ids) = Build();
            var keep = m.Query is { } q ? new JsonArray([.. Tasks.Where(t => SearchKeeps(board, q).Contains(ids[t.Key])).Select(t => (JsonNode)t.Key)]) : null;
            if (m.Plan is { } plan)
            {
                foreach (var op in plan)
                {
                    Apply(board, ids, op);
                }
            }

            var move = new JsonObject { ["do"] = m.Do };
            if (m.Task is not null) move["task"] = m.Task;
            if (m.Target is not null) move["target"] = m.Target;
            if (m.Zone is not null) move["zone"] = m.Zone;
            if (m.OnGroup is not null) move["group"] = m.OnGroup;
            var written = new JsonObject
            {
                ["name"] = m.Name,
                ["options"] = new JsonObject { ["group"] = m.Group, ["zoom"] = m.Zoom, ["show"] = m.Show, ["query"] = m.Query },
                ["move"] = move,
                ["tree"] = m.Plan is null ? "refused" : TreeOf(board, ids.ToDictionary(kv => kv.Value, kv => kv.Key)),
            };
            if (keep is not null)
            {
                written["keep"] = keep;
            }

            moves.Add(written);
        }

        return moves;
    }

    private static void Apply(TaskBoard board, Dictionary<string, Guid> ids, Op op)
    {
        Guid? GroupOf(string? name) => name is null ? null : board.Groups.Single(g => g.Name == name).Id;
        switch (op.Do)
        {
            case "move":
                board.Move(ids[op.Task], op.Parent is { } p ? ids[p] : null, op.Index, GroupOf(op.Group), Actor.User, T0);
                break;
            case "reorder":
                board.Reorder(ids[op.Task], ids[op.Before!], Actor.User, T0);
                break;
            case "after":
                board.ReorderAfter(ids[op.Task], ids[op.After!], Actor.User, T0);
                break;
            default:
                var parent = op.Parent is { } pk ? board.Get(ids[pk]) : null;
                var group = GroupOf(op.Group);
                var first = (parent is { Children.Count: > 0 } ? parent.Children[0] : null) ?? board.Items.FirstOrDefault(r => r.GroupId == group && r.ArchivedAt is null);
                var added = board.AddTask(new NewTask("New") { ParentId = parent?.Id, GroupId = parent is null ? group : null }, Actor.User, T0);
                ids[op.Task] = added.Id;
                if (first is not null)
                {
                    board.Reorder(added.Id, first.Id, Actor.User, T0);
                }

                break;
        }
    }

    /// <summary>Each group's name, then its top-level tasks and their subtasks, two spaces per level.</summary>
    private static JsonArray TreeOf(TaskBoard board, Dictionary<Guid, string> keys)
    {
        var lines = new JsonArray();
        void Walk(IEnumerable<WorkItem> items, int depth)
        {
            foreach (var item in items)
            {
                lines.Add(new string(' ', depth * 2) + keys[item.Id]);
                Walk(item.Children, depth + 1);
            }
        }

        foreach (var group in board.Groups)
        {
            lines.Add(group.Name);
            Walk(board.Items.Where(r => r.GroupId == group.Id), 1);
        }

        return lines;
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
