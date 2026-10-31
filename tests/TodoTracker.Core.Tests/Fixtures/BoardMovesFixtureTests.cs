using System.Text.Json.Nodes;

namespace TodoTracker.Core.Tests.Fixtures;

/// <summary>
/// tests/fixtures/board-moves.json: moving cards between the Board's columns (Inbox, Next, Doing, Done) and tasks in
/// the Tasks outline (reorder, indent, outdent, into another task or group), so the apps that make these moves on
/// their own (the iPhone, iPad and Mac app without a computer) move them exactly like the server. Each case's steps
/// are written here; what the board looks like at each check is what this version of the core does.
/// </summary>
public sealed class BoardMovesFixtureTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    /// <summary>How long finished cards stay in Done on the board (the web's DONE_DAYS).</summary>
    private const int DoneDays = 14;

    private static JsonObject Step(string action, params (string Key, JsonNode? Value)[] fields)
    {
        var step = new JsonObject { ["do"] = action };
        foreach (var (key, value) in fields)
        {
            step[key] = value;
        }

        return step;
    }

    private static JsonObject Add(string key, string? parent = null, string? stage = null, string? group = null, string? repeat = null) =>
        Step("add", ("key", key), ("title", key.ToUpperInvariant()), ("parent", parent), ("stage", stage), ("group", group), ("repeat", repeat));

    private static JsonObject On(string action, string task, params (string Key, JsonNode? Value)[] fields) =>
        Step(action, [("task", task), .. fields]);

    private static JsonObject Check() => Step("check");

    private static IEnumerable<(string Name, JsonObject[] Steps)> Cases()
    {
        yield return ("New tasks start in Inbox unless a column is chosen; subtasks aren't cards", [
            Add("a"), Add("b", stage: "next"), Add("c", stage: "doing"), Add("s", parent: "a"),
            Check(),
        ]);
        yield return ("Moving a card sets its column; Done finishes it; moving it out of Done opens it again", [
            Add("a"), Add("b"),
            On("stage", "a", ("stage", "next")),
            Check(),
            On("stage", "a", ("stage", "doing")),
            On("complete", "b"),
            Check(),
            On("stage", "b", ("stage", "next")),
            Check(),
            Add("s", parent: "a"),
            On("stage", "s", ("stage", "doing")),
            Check(),
        ]);
        yield return ("Starting a timer moves the card (or a subtask's task) to Doing", [
            Add("a"), Add("b", stage: "next"), Add("s", parent: "b"),
            On("timer", "a"),
            Check(),
            On("timer", "s"),
            Check(),
        ]);
        yield return ("A repeating card that moves on to its next time leaves Doing for Next", [
            Add("daily", repeat: "every day"),
            On("stage", "daily", ("stage", "doing")),
            On("complete", "daily"),
            Check(),
        ]);
        yield return ("Cards keep the order they're put in; only tasks next to each other reorder", [
            Add("a", stage: "next"), Add("b", stage: "next"), Add("c", stage: "next"), Add("s", parent: "a"),
            On("reorder", "c", ("before", "a")),
            Check(),
            On("reorder", "a", ("before", null)),
            Check(),
            On("reorderAfter", "c", ("after", "b")),
            Check(),
            On("reorder", "s", ("before", "b")),
            Check(),
        ]);
        yield return ("Indent puts a task last under the one above it; outdent puts it right after its parent", [
            Add("a"), Add("b"), Add("c"), Add("d"),
            On("indent", "b"),
            On("indent", "c"),
            Check(),
            On("indent", "c"),
            Check(),
            On("outdent", "c"),
            Check(),
            On("outdent", "b"),
            Check(),
            On("indent", "a"),
            On("outdent", "d"),
            Check(),
        ]);
        yield return ("A task moves into another task, or to the top of another group, but never into itself", [
            Add("a", stage: "doing"), Add("b"), Add("s", parent: "a"), Add("t", parent: "s"),
            On("move", "a", ("parent", "t")),
            On("move", "a", ("parent", "a")),
            Check(),
            On("move", "t", ("group", "Personal"), ("index", 0)),
            Check(),
            On("move", "s", ("parent", "b"), ("index", 0)),
            Check(),
            On("complete", "a"),
            On("archive", "a"),
            Check(),
            On("move", "a", ("parent", "b")),
            Check(),
        ]);
        yield return ("Moving a finished subtask out keeps it finished, in Done", [
            Add("a"), Add("s", parent: "a"),
            On("complete", "s"),
            On("outdent", "s"),
            Check(),
        ]);
    }

    [Fact]
    public void The_board_moves_fixture_is_how_this_version_moves_cards_and_tasks()
    {
        var cases = new JsonArray();
        foreach (var (name, steps) in Cases())
        {
            cases.Add(Run(name, steps));
        }

        FixtureFile.Check("board-moves.json", new JsonObject
        {
            ["about"] = "Moving cards on the Board (Inbox, Next, Doing, Done) and tasks in the Tasks outline. Each case runs its steps in order on a new board (groups Work and Personal), one minute apart from 2026-01-05T09:00:00Z; a step that the core refuses has \"fails\": true and changes nothing. Generated by the C# core (BoardMovesFixtureTests); Swift runs it (BoardMovesFixtureTests).",
            ["shape"] = new JsonObject
            {
                ["add"] = "{ do: add, key, title, parent?: key, stage?: inbox|next|doing, group?: name, repeat?: rule }",
                ["stage"] = "{ do: stage, task, stage }  (a card to a column; a finished card opens again)",
                ["complete | reopen | archive | timer"] = "{ do, task }  (timer: start timing it)",
                ["reorder"] = "{ do: reorder, task, before: key|null }  (among its siblings; null: last)",
                ["reorderAfter"] = "{ do: reorderAfter, task, after: key }",
                ["move"] = "{ do: move, task, parent?: key, group?: name, index?: n }  (no parent: to the top level of the group, or its own)",
                ["indent"] = "{ do: indent, task }  (move under the sibling above it, last; fails when there's none)",
                ["outdent"] = "{ do: outdent, task }  (move to its parent's parent, or the top level of its group, then right after its parent; fails at the top)",
                ["check"] = "{ do: check, tree, groups, columns, done, archived }: tree lists every task in order, two spaces per level; groups names each top-level task's group; columns are the Board's (finished cards from the last 14 days in Done, newest first; archived ones on none); done and archived list the finished and archived tasks",
            },
            ["cases"] = cases,
        });
    }

    private static JsonObject Run(string name, JsonObject[] steps)
    {
        var board = new TaskBoard();
        var tasks = new Dictionary<string, WorkItem>();
        var at = T0;
        var written = new JsonArray();
        foreach (var source in steps)
        {
            var step = (JsonObject)source.DeepClone();
            foreach (var key in step.Where(p => p.Value is null).Select(p => p.Key).ToList())
            {
                step.Remove(key);
            }

            if ((string?)step["do"] == "check")
            {
                Snapshot(board, tasks, at, step);
            }
            else
            {
                try
                {
                    Apply(board, tasks, step, at);
                }
                catch (Exception e) when (e is InvalidOperationException or ArgumentException or KeyNotFoundException)
                {
                    step["fails"] = true;
                }
            }

            written.Add(step);
            at = at.AddMinutes(1);
        }

        return new JsonObject { ["name"] = name, ["steps"] = written };
    }

    private static void Apply(TaskBoard board, Dictionary<string, WorkItem> tasks, JsonObject step, DateTimeOffset at)
    {
        var user = Actor.User;
        Guid Task(string field = "task") => tasks[(string)step[field]!].Id;
        Guid? Group() => step["group"] is { } g ? board.Groups.Single(x => x.Name == (string)g!).Id : null;
        switch ((string?)step["do"])
        {
            case "add":
                var spec = new NewTask((string)step["title"]!)
                {
                    ParentId = step["parent"] is { } p ? tasks[(string)p!].Id : null,
                    GroupId = Group(),
                    Stage = step["stage"] is { } s ? Enum.Parse<Stage>((string)s!, ignoreCase: true) : null,
                    Repeat = (string?)step["repeat"],
                };
                tasks[(string)step["key"]!] = board.AddTask(spec, user, at);
                break;
            case "stage":
                board.SetStage(Task(), Enum.Parse<Stage>((string)step["stage"]!, ignoreCase: true), user, at);
                break;
            case "complete":
                board.Complete(Task(), user, at);
                break;
            case "reopen":
                board.Reopen(Task(), user, at);
                break;
            case "archive":
                board.Archive(Task(), user, at);
                break;
            case "timer":
                board.StartTimer(Task(), user, at);
                break;
            case "reorder":
                board.Reorder(Task(), step["before"] is { } before ? tasks[(string)before!].Id : null);
                break;
            case "reorderAfter":
                board.ReorderAfter(Task(), Task("after"));
                break;
            case "move":
                board.Move(Task(), step["parent"] is { } into ? tasks[(string)into!].Id : null, (int?)step["index"], Group(), user, at);
                break;
            case "indent":
                {
                    // What the apps send for Tab (outlinemodel.js indentRequests): under the sibling above, last.
                    var item = board.Get(Task());
                    var siblings = item.Parent?.Children ?? board.Items;
                    var i = siblings.ToList().IndexOf(item);
                    if (i < 1)
                    {
                        throw new InvalidOperationException("Nothing above to indent under.");
                    }

                    board.Move(item.Id, siblings[i - 1].Id, null, null, user, at);
                    break;
                }

            case "outdent":
                {
                    // Shift+Tab (outdentRequests): out one level, right after its parent.
                    var item = board.Get(Task());
                    var parent = item.Parent ?? throw new InvalidOperationException("Already at the top level.");
                    board.Move(item.Id, parent.Parent?.Id, null, parent.Parent is null ? parent.GroupId : null, user, at);
                    board.ReorderAfter(item.Id, parent.Id);
                    break;
                }

            default:
                throw new InvalidDataException($"Unknown step {step["do"]}");
        }
    }

    private static void Snapshot(TaskBoard board, Dictionary<string, WorkItem> tasks, DateTimeOffset at, JsonObject step)
    {
        var keys = tasks.ToDictionary(kv => kv.Value.Id, kv => kv.Key);
        string Key(WorkItem item) => keys[item.Id];
        var tree = new JsonArray();
        void Walk(WorkItem item, int depth)
        {
            tree.Add(new string(' ', depth * 2) + Key(item));
            foreach (var child in item.Children)
            {
                Walk(child, depth + 1);
            }
        }

        foreach (var root in board.Items)
        {
            Walk(root, 0);
        }

        var groups = new JsonObject();
        foreach (var root in board.Items)
        {
            groups[Key(root)] = board.Groups.Single(g => g.Id == root.GroupId).Name;
        }

        var columns = new JsonObject
        {
            ["inbox"] = new JsonArray(),
            ["next"] = new JsonArray(),
            ["doing"] = new JsonArray(),
        };
        var recent = new List<WorkItem>();
        foreach (var root in board.Items.Where(r => r.ArchivedAt is null))
        {
            if (root.IsDone)
            {
                if (root.CompletedAt >= at.AddDays(-DoneDays))
                {
                    recent.Add(root);
                }
            }
            else
            {
                ((JsonArray)columns[root.Stage.ToString().ToLowerInvariant()]!).Add(Key(root));
            }
        }

        columns["done"] = new JsonArray([.. recent.OrderByDescending(r => r.CompletedAt).Select(r => (JsonNode)Key(r))]);
        step["tree"] = tree;
        step["groups"] = groups;
        step["columns"] = columns;
        step["done"] = new JsonArray([.. Flat(board).Where(i => i.IsDone).Select(i => (JsonNode)Key(i))]);
        step["archived"] = new JsonArray([.. board.Items.Where(i => i.ArchivedAt is not null).Select(i => (JsonNode)Key(i))]);
    }

    private static IEnumerable<WorkItem> Flat(TaskBoard board)
    {
        IEnumerable<WorkItem> Walk(WorkItem item) => [item, .. item.Children.SelectMany(Walk)];
        return board.Items.SelectMany(Walk);
    }
}
