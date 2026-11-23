using System.Globalization;
using System.Text;
using OneThing.Core;
using OneThing.Core.Vault;
using OneThing.Server;

namespace OneThing.Cli;

internal sealed record CliCommand(string Name, string Usage, string Summary, string? Details, Func<CliSession, CliArgs, Task> Run);

/// <summary>Every <c>ot</c> command, its help, and what it does.</summary>
internal static partial class CliCommands
{
    public static readonly IReadOnlyDictionary<string, CliCommand> All = new[]
    {
        new CliCommand("now", "ot [now] [filter] [-g group]", "What to do now, and what is waiting", "filter uses the list syntax, e.g. ot now #release", Now),
        new CliCommand("add", "ot add <title…> [-g group] [--under task] [--details text] [--tag t]… [--label l]… [--due when] [--snooze when] [--energy 1|2|3] [--repeat rule] [--icon emoji] [--color #rrggbb]", "Add a task",
            "Quick words in the title: ! high, !! critical, !low, @2h or @tomorrow (defer), due:3d or due:2026-02-01, #tag.\n" +
            "Quote the title: shells treat # ! @ specially. Example: ot add 'Renew passport !! due:7d #admin'\n" +
            "Or use options: ot add Renew passport --due 7d --tag admin\n" +
            "-g takes a group that exists (ot groups); ot group add makes a new one.", Add),
        new CliCommand("list", "ot list [filter] [-g group] [--all]", "Find tasks",
            "Words match titles and details; #tag, label:name, group:name, is:open, is:done narrow it. Open tasks unless --all or is:.", List),
        new CliCommand("show", "ot show <task>", "A task with its subtasks, notes, file and link", null, Show),
        new CliCommand("open", "ot open <task>", "Open a task in the app (its onething:// link)", "ot show prints the link, to paste in a note, chat or mail.", Open),
        new CliCommand("done", "ot done <task> | ot done <id> <id>…", "Mark tasks done", "Several tasks at once by id (as ot prints them); words are one task's title.", Done),
        new CliCommand("reopen", "ot reopen <task>", "Undo done", null, Reopen),
        new CliCommand("first", "ot first <task> | ot first <id> <id>…", "Put tasks at the top of Do now (the first becomes the focus)", null, First),
        new CliCommand("note", "ot note <task> <text…|-> [--url https://… [--title page]] | ot note <task> --edit <note|last> <text…|->", "Log progress (\"did X, next Y\"); - reads the text from stdin",
            "--url: the web page the note is about. --edit corrects a note (its id from ot show --json, or last); with --as, only a note that agent wrote.", Note),
        new CliCommand("snooze", "ot snooze <task> <when | after <task> | clear>", "Defer a task until later, or until another task is done (it moves to Waiting and reminds you)",
            "when: 45m, 2h, 3d, tomorrow, next week, fri 14:30, weekend, 2026-02-01, 2026-02-01T14:30 — or: after <other task>.\nclear brings it back to Do now.", Snooze),
        new CliCommand("remind", "ot remind <task> <when…> [--message text] | ot remind <task> --dismiss <reminder|all>", "Add a reminder without deferring the task, or dismiss one",
            "when: the same words as ot snooze (2h, tomorrow 9:00, fri 14:30). A reminder's id is in ot show --json.", Remind),
        new CliCommand("edit", "ot edit <task> [--title t] [--details d] [--priority low|normal|high|critical] [--due when|none] [--energy 1|2|3|none] [--repeat rule|none] [--icon emoji|none] [--color #rrggbb|none] [--sequential on|off] [--step-delay hours|none]", "Change a task",
            "--energy: how much energy it takes per hour, 1 (light) to 3 (draining).\n" +
            "--repeat: every weekday, every 2 weeks on Monday, Thursday, every month on the last Friday (+ when done, until 2026-12-31, for 5 times).\n" +
            "--sequential on: its subtasks are steps, one at a time; --step-delay: hours between finishing one step and the next coming up.", Edit),
        new CliCommand("energy", "ot energy [per-day | default]", "Energy today: what you have in a day and what today's plan takes",
            "A day's plan takes the hours of its blocks times each task's energy (1 to 3 an hour; none counts as 1). ot energy 8 sets 8 a day; ot energy default goes back to 10.", EnergyCommand),
        new CliCommand("tag", "ot tag <task> [+]tag… -tag…", "Add or remove tags", null, (s, a) => Tags(s, a, labels: false)),
        new CliCommand("label", "ot label <task> [+]label… -label…", "Add or remove labels (quote names with spaces)", null, (s, a) => Tags(s, a, labels: true)),
        new CliCommand("move", "ot move <task> (--under task | --top | -g group) [--index n] | ot move <task> (--before task | --after task)", "Put a task under another, at the top level, or in another group; or next to a sibling",
            "--before and --after reorder a task among its siblings (subtasks under one parent, or tasks in a column).", Move),
        new CliCommand("order", "ot order <task> <task>…", "Arrange Do now in this order (the first is the focus)", "Ids as ot prints them. ot first puts some at the top and leaves the rest as they are.", Order),
        new CliCommand("steps", "ot steps <task> <step>… [--delay hours]", "Add ordered steps; finishing one unlocks the next after the delay", null, Steps),
        new CliCommand("attach", "ot attach <task> <file>", "Copy a file into the tasks folder and link it", null, Attach),
        new CliCommand("history", "ot history [<task> [--text version]] [--limit n]", "Saved versions, newest first: of the whole tasks folder, or of one task (--text prints that version)",
            "Every change is saved as a version (it needs git). ot restore puts a task back.", History),
        new CliCommand("restore", "ot restore <task> <version>", "Put a task back the way it was in a version", null, Restore),
        new CliCommand("groups", "ot groups", "Groups (tabs) with counts", "ot group adds, renames and recolors them.", GroupCommand),
        new CliCommand("group", "ot group (add <name> [--color #rrggbb] | rename <group> <new name> | color <group> <#rrggbb | none>)", "Make a group (tab), rename it or change its color",
            "Quote a group name with spaces when it comes before more words: ot group rename \"Side projects\" Hobbies.\n" +
            "Groups are deleted only in the app (its tasks move to another group there).", GroupCommand),
        new CliCommand("timeline", "ot timeline [<task>] [--limit n]", "What happened, newest first: on one task (with its subtasks) or the whole board",
            "Each entry says who did it: you, an agent (by name), a connector or the tasks folder.", Timeline),
        new CliCommand("labels", "ot labels [add <name> [--color #rrggbb] | rename <label> <new name> | color <label> <#rrggbb>]", "The curated labels: list, add, rename, recolor",
            "ot label <task> +name puts one on a task. Labels are deleted only in the app.", Labels),
        new CliCommand("tags", "ot tags", "The tags on open tasks, most used first", "ot tag <task> +x -y changes a task's tags.", TagList),
        new CliCommand("vault", "ot vault [guide | obsidian | use <folder|obsidian vault>]", "Where the task files live, their format, or switch folders", "use is for you, the person at the computer (not for AI agents); the app switches at its next start.", Vault),
        new CliCommand("mcp", "onething mcp", "Run the MCP server over stdio (for Claude, Copilot, VS Code…)", "See docs/ai-connectors.md for setup in each app.", (_, _) => Task.CompletedTask),
        new CliCommand("start", "ot start <task>", "Start timing a task (any other timer stops)", "The task's card moves to Doing. ot stop stops it.", Start),
        new CliCommand("stop", "ot stop", "Stop the timer", null, Stop),
        new CliCommand("stage", "ot stage <task> <inbox|next|doing>", "Move a task to a board column", "Finish a task (ot done) to move it to Done.", Stage),
        new CliCommand("archive", "ot archive <task> | ot archive --done-before <days>", "Put finished tasks away (ot list is:archived finds them)", "--done-before 14 archives everything finished at least 14 days ago.\nThere's no ot delete: archived tasks are kept (ot unarchive brings one back); deleting is in the app, for you.", Archive),
        new CliCommand("unarchive", "ot unarchive <task>", "Bring an archived task back to the done list", null, Unarchive),
        new CliCommand("time", "ot time [--days n] [-g group]", "Where the time went: tracked per task, focus sessions, tasks done", "Default: the last 28 days. --json prints the full report (per day, group, hour).", Time),
        new CliCommand("log", "ot log <task> <duration> [--at when] | ot log <task> <duration> --edit <entry|last> [--at when]", "Record time already spent on a task, or correct an entry",
            "duration: 45m, 1h30m, 1.5h or minutes (at most 24 hours). --at: when it started (default: it ended now). An entry's id is in ot show --json.\n" +
            "A time entry is removed only in the app; --edit corrects one.", Log),
        new CliCommand("focus", "ot focus [start [<task>] | pause | resume | skip | reset | next [<task>] | lengths [--focus m] [--break m] [--long-break m] [--sessions n]]", "The focus (Pomodoro) timer: what it's doing, or start, pause, resume, skip, reset; its lengths",
            "start without a task focuses on nothing in particular; next starts the next session (on the last session's task, or today's focus).\n" +
            "lengths shows how long focus, a break and a long break last (minutes) and how many sessions come before a long break; with options it changes those and leaves the rest (focus 1-180, break 1-60, long break 1-120, sessions 1-12). A new length counts from the next session.", Focus),
        new CliCommand("plan", "ot plan [--from yyyy-mm-dd] [--days n] [-g group] | ot plan <task> <when…> [--minutes n] | ot plan <task> off | ot plan settings [options]", "The plan: blocks of time on tasks",
            "when: 2026-01-05T14:00, tomorrow 9:00, fri 14:30. --minutes: 5 to 720 (default what it had, else 30).\n" +
            "settings [-g group] [--view day|week] [--hours work|full] [--work-hours 9:00-17:00] [--work-days 1-5] [--week-start mon|sun]: how the Plan view looks (days 1 = Monday … 7 = Sunday).", Plan),
        new CliCommand("hooks", "ot hooks [list | add <event> | edit <id> | enable <id> | disable <id> | approve <id> <version> | reject <id> <version> | remove <id> | test [event] | path]", "Run your own scripts or webhooks when something happens (hooks.json)",
            "add <event> (--powershell script | --bash script | --url https://…) [--name n] [--timeout seconds] [-g group]… [--tag t]… [--label l]… [--actor user|agent|…]…\n" +
            "edit <id> [--event e] and any of the add options (only what you give changes).\n" +
            "<event>: taskCompleted, taskCreated, reminderDue, noteAdded, focusEnded… or * for every event (see docs/hooks.md).\n" +
            "A command gets the event as JSON on stdin. A hook an AI agent adds or changes (--as or ONETHING_AGENT) waits for your approval:\n" +
            "approving is for you, the person at the computer: ot hooks approve <id> <version> (ot hooks list shows both), or Settings › Hooks in the app; reject turns it down.\n" +
            "test runs the hooks for an event now, with an example task.", Hooks),
        new CliCommand("rich", "ot rich <task> [<file.html> | - | none]", "A task's rich HTML version (tables, colors): print it, save one from a file or stdin, or remove it",
            "Only top-level tasks have one. Scripts in it never run. Only none removes it: empty input or an empty file is refused.", Rich),
        new CliCommand("attachment", "ot attachment <task> <file name or id> [--to path | --to -]", "Save a copy of a task's attachment (default: here, by its name; - writes it to stdout as it is)",
            "ot attach adds one. Attachments are removed only in the app.", AttachmentSave),
        new CliCommand("export", "ot export [--to file [--force]]", "The whole board as JSON (the format every app reads)",
            "--to never replaces a file unless --force, and never writes into the tasks folder.", Export),
        new CliCommand("settings", "ot settings [theme system|light|dark | teams <https://…|off>]", "The app's settings: the theme, and where reminders are posted (Teams)",
            "teams is for you, the person at the computer (not for AI agents). A running app keeps the settings it read until it starts again or you change one in it.", SettingsCommand),
        new CliCommand("sync", "ot sync [now]", "How syncing with your other computers is going; now syncs (while the app isn't running)",
            "Setting it up (where, which library, clashes) is in the app: Settings › Sync. While the app runs it syncs by itself, and ot sync now says so. Syncing now is yours: ot sync now refuses --as (the status doesn't).", Sync),
        new CliCommand("connectors", "ot connectors [sync <id> | sync github <group>]", "Notion, Microsoft To Do and GitHub Issues: their status; sync one now (while the app isn't running)",
            "Signing in and choosing what to connect are in the app: Settings › Connected apps. Syncing now is yours (it starts one that isn't syncing yet, like Start syncing in the app): ot connectors sync refuses --as (the status doesn't).", Connectors),
        new CliCommand("plugins", "ot plugins [on <id> | off <id>]", "The extras (plugins) and whether each is on; switch one on or off",
            "Switching is for you, the person at the computer (not for AI agents); it applies when the app starts again.", Plugins),
    }.ToDictionary(c => c.Name, StringComparer.Ordinal);

    public static string Help(string? topic)
    {
        var text = new StringBuilder();
        if (topic is not null && All.TryGetValue(topic, out var command))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{command.Summary}.").AppendLine().AppendLine(CultureInfo.InvariantCulture, $"  {command.Usage}");
            if (command.Details is { } details)
            {
                text.AppendLine().AppendLine(details);
            }

            text.AppendLine().AppendLine("<task> is an id (or its first 6+ characters, as ot prints them) or words from the title.");
            return text.ToString();
        }

        text.AppendLine("ot — One Thing in your terminal and for AI agents (also: onething)").AppendLine().AppendLine("Commands:");
        var width = All.Keys.Max(k => k.Length) + 2;
        foreach (var c in All.Values)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {c.Name.PadRight(width)}{c.Summary}");
        }

        text.AppendLine().AppendLine("Options for every command:");
        foreach (var (option, what) in GlobalOptions)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {option,-16}{what}");
        }

        text.AppendLine().AppendLine("ot help <command> shows details. <task> is an id prefix or words from the title.");
        return text.ToString();
    }

    /// <summary>The options every command takes (ot help and docs/cli.md list them).</summary>
    public static readonly IReadOnlyList<(string Option, string What)> GlobalOptions =
    [
        ("--json", "print JSON (same shapes as the REST API and MCP tools)"),
        ("--as <name>", "attribute changes to an agent, e.g. --as claude (or set ONETHING_AGENT)"),
        ("--vault <dir>", "use another tasks folder (default: the app's, or ONETHING_VAULT)"),
        ("--data <dir>", "use another app data folder (or ONETHING_DATA)"),
        ("--no-history", "don't save a version for this change"),
    ];

    private static async Task Now(CliSession s, CliArgs a)
    {
        a.Allow("group");
        var filter = a.Words.Count > 0 ? string.Join(' ', a.Words) : null;
        var dashboard = await s.Read(b => Wire.Dashboard(b, s.Now, Group(b, a.Value("group")), 5, s.Links, filter)).ConfigureAwait(false);
        await s.Print(dashboard, (o, d) => o.Dashboard(d)).ConfigureAwait(false);
    }

    private static async Task Add(CliSession s, CliArgs a)
    {
        a.Allow("group", "under", "details", "tag", "label", "due", "snooze", "energy", "repeat", "icon", "color");
        var text = a.WordsFrom(0, "what to add");
        var item = await s.Change((b, now) =>
        {
            var capture = QuickCaptureParser.Parse(text, now, s.Zone);
            var parent = a.Value("under") is { } under ? TaskResolver.Resolve(b, under, i => !i.IsDone) : null;
            var created = b.AddTask(
                new NewTask(capture.Title)
                {
                    ParentId = parent?.Id,
                    GroupId = Group(b, a.Value("group")),
                    Priority = capture.Priority,
                    Details = a.Value("details"),
                    Deadline = a.Value("due") is { } due ? CliTime.Deadline(due, now, s.Zone) : capture.Deadline,
                    Tags = [.. capture.Tags, .. a.Values("tag")],
                    Labels = a.Values("label").Count > 0 ? a.Values("label") : null,
                    Energy = a.Value("energy") is { } level ? EnergyLevel(level) : null,
                    Repeat = a.Value("repeat"),
                    Icon = a.Value("icon"),
                    Color = Color(a.Value("color")),
                },
                s.Actor,
                now);
            var defer = a.Value("snooze") is { } snooze ? CliTime.Defer(snooze, now, s.Zone) : capture.NextActionAt;
            if (defer is { } at)
            {
                b.ScheduleNextAction(created.Id, at, s.Actor, now, notify: true);
            }

            return created;
        }).ConfigureAwait(false);
        await s.Print(item, (o, i) => o.Changed("Added", i, withGroup: true)).ConfigureAwait(false);
    }

    private static async Task List(CliSession s, CliArgs a)
    {
        a.Allow("group", "all");
        var filter = string.Join(' ', a.Words);
        if (a.Value("group") is { } group)
        {
            filter += group.Contains(' ', StringComparison.Ordinal) ? $" group:\"{group}\"" : $" group:{group}";
        }

        var includeDone = a.Has("all") || TaskQuery.Parse(filter).HasState;
        var hits = await s.Read(b => TaskQuery.Parse(filter).Apply(b)
            .Where(i => includeDone || !i.IsDone)
            .Select(i => Wire.SearchHit(i, s.Now, b, s.Links))
            .ToList()).ConfigureAwait(false);
        await s.Print(hits, (o, h) => o.Hits(h, filter)).ConfigureAwait(false);
    }

    private static async Task Show(CliSession s, CliArgs a)
    {
        a.Allow();
        var reference = a.WordsFrom(0, "which task");
        var item = await s.Read(b => Wire.Item(TaskResolver.Resolve(b, reference), s.Now, b, s.Links)).ConfigureAwait(false);
        await s.Print(item, (o, i) => o.Item(i, s.Links.Vault.RootPath)).ConfigureAwait(false);
    }

    private static async Task Open(CliSession s, CliArgs a)
    {
        a.Allow();
        var reference = a.WordsFrom(0, "which task");
        var item = await s.Read(b => TaskResolver.Resolve(b, reference)).ConfigureAwait(false);
        var link = TaskLinks.ForTask(item.Id);
        try
        {
            s.Context.OpenLink(link);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new InvalidOperationException($"Couldn't open {link} ({ex.Message}). Is the One Thing app installed and started once?", ex);
        }

        await s.Print(new { item.Id, item.Title, Link = link }, (o, x) => o.Line($"Opened {x.Title}  {x.Link}")).ConfigureAwait(false);
    }

    private static async Task Done(CliSession s, CliArgs a)
    {
        a.Allow();
        _ = a.Word(0, "which task");

        // Several tasks only when every word is an id; otherwise the words are one title ("ot done Renew passport").
        // All of them are found first, then done in one change: a wrong id leaves every task as it was.
        IReadOnlyList<string> references = a.Words.Count > 1 && a.Words.All(TaskResolver.LooksLikeId) ? a.Words : [a.WordsFrom(0, "which task")];
        var ids = await s.Store.UpdateAsync(b =>
        {
            var now = s.Now;
            var items = references.Select(r => TaskResolver.Resolve(b, r, i => !i.IsDone).Id).Distinct().ToList();
            foreach (var id in items)
            {
                b.Complete(id, s.Actor, now);
            }

            return items;
        }).ConfigureAwait(false);
        await s.SaveVersion().ConfigureAwait(false);
        var done = new List<ItemDto>();
        foreach (var id in ids)
        {
            done.Add(await s.Item(id).ConfigureAwait(false));
        }

        if (done.Count == 1)
        {
            await s.Print(done[0], (o, i) => o.Changed("Done", i)).ConfigureAwait(false);
        }
        else
        {
            await s.Print(done, (o, items) => items.ForEach(i => o.Changed("Done", i))).ConfigureAwait(false);
        }
    }

    private static async Task First(CliSession s, CliArgs a)
    {
        a.Allow();
        _ = a.Word(0, "which task");
        IReadOnlyList<string> references = a.Words.Count > 1 && a.Words.All(TaskResolver.LooksLikeId) ? a.Words : [a.WordsFrom(0, "which task")];
        await s.Store.UpdateAsync(b =>
        {
            b.PutFirst(references.Select(r => TaskResolver.Resolve(b, r, i => !i.IsDone).Id).ToList(), s.Actor, s.Now);
            return true;
        }).ConfigureAwait(false);
        await s.SaveVersion().ConfigureAwait(false);
        var dashboard = await s.Read(b => Wire.Dashboard(b, s.Now, null, 5, s.Links)).ConfigureAwait(false);
        await s.Print(dashboard, (o, d) => o.Dashboard(d)).ConfigureAwait(false);
    }

    private static async Task Reopen(CliSession s, CliArgs a)
    {
        a.Allow();
        var reference = a.WordsFrom(0, "which task");
        var item = await s.Change((b, now) =>
        {
            var found = TaskResolver.Resolve(b, reference, i => i.IsDone);
            b.Reopen(found.Id, s.Actor, now);
            return found;
        }).ConfigureAwait(false);
        await s.Print(item, (o, i) => o.Changed("Reopened", i)).ConfigureAwait(false);
    }

    private static async Task Start(CliSession s, CliArgs a)
    {
        a.Allow();
        var reference = a.WordsFrom(0, "which task");
        var timer = await s.Store.UpdateAsync(b =>
        {
            b.StartTimer(TaskResolver.Resolve(b, reference, i => !i.IsDone).Id, s.Actor, s.Now);
            return Wire.Timer(b, s.Now);
        }).ConfigureAwait(false);
        await s.SaveVersion().ConfigureAwait(false);
        await s.Print(timer, (o, t) => o.Line($"Timing \"{t.Title}\" (ot stop stops it)")).ConfigureAwait(false);
    }

    private static async Task Stop(CliSession s, CliArgs a)
    {
        a.Allow();
        var (stopped, timer) = await s.Store.UpdateAsync(b =>
        {
            var running = b.StopTimer(s.Actor, s.Now);
            return (running, Wire.Timer(b, s.Now));
        }).ConfigureAwait(false);
        await s.SaveVersion().ConfigureAwait(false);
        await s.Print(timer, (o, _) => o.Line(stopped is { } r
            ? $"Stopped \"{r.Item.Title}\" after {TaskMarkdownDuration(r.Entry.Duration(s.Now))}"
            : "No timer was running")).ConfigureAwait(false);
    }

    private static async Task Stage(CliSession s, CliArgs a)
    {
        a.Allow();
        var reference = a.Word(0, "which task");
        var stage = BoardTimeEndpoints.ParseStage(a.Word(1, "which column (inbox, next or doing)"));
        var item = await s.Change((b, now) =>
        {
            var found = TaskResolver.Resolve(b, reference, i => i.Parent is null);
            b.SetStage(found.Id, stage, s.Actor, now);
            return found;
        }).ConfigureAwait(false);
        await s.Print(item, (o, i) => o.Changed($"Moved to {stage}:", i)).ConfigureAwait(false);
    }

    private static async Task Archive(CliSession s, CliArgs a)
    {
        a.Allow("done-before");
        if (a.Value("done-before") is { } days)
        {
            var older = int.TryParse(days.TrimEnd('d'), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new ArgumentException("--done-before takes a number of days, e.g. 14.");
            var archived = await s.Store.UpdateAsync(b => new ArchiveResult(b.ArchiveCompleted(s.Now.AddDays(-older).AddTicks(1), null, s.Actor, s.Now))).ConfigureAwait(false);
            await s.SaveVersion().ConfigureAwait(false);
            await s.Print(archived, (o, r) => o.Line(r.Archived.Count == 0 ? "Nothing to archive" : $"Archived {r.Archived.Count} finished task(s)")).ConfigureAwait(false);
            return;
        }

        var reference = a.WordsFrom(0, "which task");
        var item = await s.Change((b, now) =>
        {
            var found = TaskResolver.Resolve(b, reference, i => i.IsDone && i.Parent is null);
            b.Archive(found.Id, s.Actor, now);
            return found;
        }).ConfigureAwait(false);
        await s.Print(item, (o, i) => o.Changed("Archived", i)).ConfigureAwait(false);
    }

    private static async Task Time(CliSession s, CliArgs a)
    {
        a.Allow("days", "group");
        var days = 28;
        if (a.Value("days") is { } text && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out days) || days is < 1 or > TimeReport.MaxDays))
        {
            throw new ArgumentException($"--days takes a number of days from 1 to {TimeReport.MaxDays}.");
        }
        var report = await s.Read(b =>
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(s.Now, s.Zone).DateTime);
            return TimeReport.Build(b, today.AddDays(-(Math.Max(1, days) - 1)), today, s.Zone, s.Now, Group(b, a.Value("group")));
        }).ConfigureAwait(false);
        await s.Print(report, (o, r) =>
        {
            o.Line($"Last {r.Days.Count} days: {TaskMarkdownDuration(TimeSpan.FromSeconds(r.TrackedSeconds))} tracked · {r.FocusSessions} focus sessions · {r.Completed} done · longest streak {r.LongestStreak} day(s)");
            foreach (var task in r.Tasks)
            {
                o.Line($"  {TaskMarkdownDuration(TimeSpan.FromSeconds(task.TrackedSeconds)),12}  {task.Title}{(task.Done ? " ✓" : string.Empty)}");
            }
        }).ConfigureAwait(false);
    }

    private static string TaskMarkdownDuration(TimeSpan span) => OneThing.Core.Vault.TaskMarkdown.DurationLabel(span);

    private static async Task Note(CliSession s, CliArgs a)
    {
        a.Allow("url", "title", "edit");
        if (a.Has("title") && !a.Has("url"))
        {
            throw new CliUsageException("--title is the title of the page in --url.");
        }

        var reference = a.Word(0, "which task");
        var fromInput = a.Words.Count == 2 && a.Words[1] == "-";
        if (fromInput && ReferenceEquals(s.Context.In, Console.In) && !Console.IsInputRedirected)
        {
            await s.Context.Error.WriteLineAsync(OperatingSystem.IsWindows() ? "Type the note, then Ctrl+Z and Enter:" : "Type the note, then Ctrl+D:").ConfigureAwait(false);
        }

        var text = fromInput
            ? (await s.Context.In.ReadToEndAsync().ConfigureAwait(false)).Trim().ReplaceLineEndings("\n")
            : a.WordsFrom(1, "the note");
        NoteDto? note = null;
        await s.Change((b, now) =>
        {
            var item = TaskResolver.Resolve(b, reference);
            note = a.Value("edit") is { } which
                ? Wire.Note(item, b.UpdateNote(item.Id, Pick(item.Notes, n => n.Id, n => n.At, which, "note").Id, text, s.Actor, now))
                : Wire.Note(item, b.AddNote(item.Id, text, s.Actor, now, a.Value("url"), a.Value("title")));
            return item;
        }).ConfigureAwait(false);
        await s.Print(note!, (o, n) => o.Line($"{(a.Has("edit") ? "Changed the note" : "Noted")} on {TaskResolver.ShortId(n.ItemId)} \"{n.ItemTitle}\"")).ConfigureAwait(false);
    }

    private static async Task Snooze(CliSession s, CliArgs a)
    {
        a.Allow();
        var reference = a.Word(0, "which task");
        var when = a.WordsFrom(1, "when (e.g. 2h, tomorrow, next week, after <task>)");
        var item = await s.Change((b, now) =>
        {
            var found = TaskResolver.Resolve(b, reference, i => !i.IsDone);
            if (when.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                b.ClearNextAction(found.Id, s.Actor, now);
            }
            else if (when.StartsWith("after ", StringComparison.OrdinalIgnoreCase))
            {
                var first = TaskResolver.Resolve(b, when["after ".Length..].Trim(), i => !i.IsDone && i.Id != found.Id);
                b.WaitFor(found.Id, first.Id, s.Actor, now);
            }
            else
            {
                b.ScheduleNextAction(found.Id, CliTime.Defer(when, now, s.Zone), s.Actor, now, notify: true);
            }

            return found;
        }).ConfigureAwait(false);
        await s.Print(item, (o, i) => o.Changed(when.Equals("clear", StringComparison.OrdinalIgnoreCase) ? "Back in Do now:" : "Snoozed", i)).ConfigureAwait(false);
    }

    private static async Task Edit(CliSession s, CliArgs a)
    {
        string[] fields = ["title", "details", "priority", "due", "energy", "repeat", "icon", "color", "sequential", "step-delay"];
        a.Allow(fields);
        var reference = a.WordsFrom(0, "which task");
        if (!fields.Any(a.Has))
        {
            throw new CliUsageException("Say what to change: --title, --details, --priority, --due, --energy, --repeat, --icon, --color, --sequential or --step-delay.");
        }

        static bool None(string? text) => text is not null && text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);
        var stepDelay = a.Value("step-delay");
        TimeSpan? delay = stepDelay is null || None(stepDelay) ? null : StepDelay(stepDelay);

        var energy = a.Value("energy");
        var noEnergy = energy is not null && energy.Equals("none", StringComparison.OrdinalIgnoreCase);

        var item = await s.Change((b, now) =>
        {
            var found = TaskResolver.Resolve(b, reference);
            var due = a.Value("due");
            var clear = due is not null && (due.Equals("none", StringComparison.OrdinalIgnoreCase) || due.Equals("never", StringComparison.OrdinalIgnoreCase));
            b.Update(
                found.Id,
                new TaskChanges
                {
                    Title = a.Value("title"),
                    Details = a.Value("details"),
                    Priority = a.Value("priority") is { } p ? Wire.ParsePriority(p) : null,
                    Deadline = due is not null && !clear ? CliTime.Deadline(due, now, s.Zone) : null,
                    ClearDeadline = clear,
                    Energy = energy is not null && !noEnergy ? EnergyLevel(energy) : null,
                    ClearEnergy = noEnergy,
                    Repeat = None(a.Value("repeat")) ? null : a.Value("repeat"),
                    ClearRepeat = None(a.Value("repeat")),
                    Icon = None(a.Value("icon")) ? null : a.Value("icon"),
                    ClearIcon = None(a.Value("icon")),
                    Color = Color(a.Value("color")),
                    ClearColor = None(a.Value("color")),
                    Sequential = a.Value("sequential") is { } on ? OnOff(on, "--sequential") : null,
                    StepDelay = delay,
                    ClearStepDelay = None(stepDelay),
                },
                s.Actor,
                now);
            return found;
        }).ConfigureAwait(false);
        await s.Print(item, (o, i) => o.Changed("Updated", i)).ConfigureAwait(false);
    }

    private static int EnergyLevel(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var level) && level is >= Energy.Min and <= Energy.Max
            ? level
            : throw new CliUsageException("--energy is 1, 2 or 3 (energy per hour), or none.");

    private static async Task EnergyCommand(CliSession s, CliArgs a)
    {
        a.Allow();
        var set = string.Join(' ', a.Words).Trim();
        EnergyDto energy;
        if (string.IsNullOrWhiteSpace(set))
        {
            energy = await s.Read(b => Wire.EnergyToday(b, s.Now, s.Zone)).ConfigureAwait(false);
        }
        else
        {
            int? perDay = set.Equals("default", StringComparison.OrdinalIgnoreCase) ? null
                : int.TryParse(set, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= Energy.MaxPerDay ? n
                : throw new CliUsageException($"ot energy takes a number from 1 to {Energy.MaxPerDay}, or default.");
            energy = await s.Store.UpdateAsync(b =>
            {
                b.SetEnergyPerDay(perDay, s.Actor, s.Now);
                return Wire.EnergyToday(b, s.Now, s.Zone);
            }).ConfigureAwait(false);
        }

        await s.Print(energy, (o, e) => o.Energy(e)).ConfigureAwait(false);
    }

    private static async Task Tags(CliSession s, CliArgs a, bool labels)
    {
        a.Allow();
        var reference = a.Word(0, "which task");
        _ = a.Word(1, labels ? "which labels (+name or -name)" : "which tags (+tag or -tag)");
        var item = await s.Change((b, now) =>
        {
            var found = TaskResolver.Resolve(b, reference);
            var current = (labels ? found.Labels : found.Tags).ToList();
            foreach (var word in a.Words.Skip(1))
            {
                var remove = word.StartsWith('-');
                var name = word.TrimStart('+', '-').Trim().TrimStart('#');
                current.RemoveAll(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
                if (!remove)
                {
                    current.Add(name);
                }
            }

            if (labels)
            {
                b.SetLabels(found.Id, current, s.Actor, now);
            }
            else
            {
                b.SetTags(found.Id, current, s.Actor, now);
            }

            return found;
        }).ConfigureAwait(false);
        await s.Print(item, (o, i) => o.Changed(labels ? "Labels on" : "Tags on", i, withTags: true)).ConfigureAwait(false);
    }

    private static async Task Move(CliSession s, CliArgs a)
    {
        a.Allow("under", "top", "group", "index", "before", "after");
        var reference = a.WordsFrom(0, "which task");
        if (a.Has("before") || a.Has("after"))
        {
            if (a.Has("under") || a.Has("top") || a.Has("group") || a.Has("index") || (a.Has("before") && a.Has("after")))
            {
                throw new CliUsageException("--before or --after alone: it reorders the task among its siblings.");
            }

            var reordered = await s.Change((b, now) =>
            {
                var found = TaskResolver.Resolve(b, reference);
                if (a.Value("before") is { } next)
                {
                    b.Reorder(found.Id, TaskResolver.Resolve(b, next, i => i.Id != found.Id).Id, s.Actor, now);
                }
                else
                {
                    b.ReorderAfter(found.Id, TaskResolver.Resolve(b, a.Value("after")!, i => i.Id != found.Id).Id, s.Actor, now);
                }

                return found;
            }).ConfigureAwait(false);
            await s.Print(reordered, (o, i) => o.Changed("Moved", i)).ConfigureAwait(false);
            return;
        }

        if (!a.Has("under") && !a.Has("top") && !a.Has("group"))
        {
            throw new CliUsageException("Say where: --under <task>, --top, --group <group>, --before <task> or --after <task>.");
        }

        int? index = a.Value("index") is { } text
            ? int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : throw new CliUsageException("--index must be 0 or more.")
            : null;
        var item = await s.Change((b, now) =>
        {
            var found = TaskResolver.Resolve(b, reference);
            var group = Group(b, a.Value("group"));
            if (a.Value("under") is { } under)
            {
                b.Move(found.Id, TaskResolver.Resolve(b, under, i => !i.IsDone).Id, index, null, s.Actor, now);
            }
            else if (a.Has("top") || found.Parent is not null)
            {
                b.Move(found.Id, null, index, group, s.Actor, now);
            }
            else
            {
                b.MoveToGroup(found.Id, group!.Value, s.Actor, now);
            }

            return found;
        }).ConfigureAwait(false);
        await s.Print(item, (o, i) => o.Changed("Moved", i, withGroup: true)).ConfigureAwait(false);
    }

    /// <summary>The longest wait between steps: a year.</summary>
    private const double MaxStepDelayHours = 24 * 366;

    private static TimeSpan StepDelay(string text, string option = "--step-delay") =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) && hours >= 0 && hours <= MaxStepDelayHours
            ? TimeSpan.FromHours(hours)
            : throw new CliUsageException($"{option} is in hours, from 0 to {MaxStepDelayHours.ToString(CultureInfo.InvariantCulture)} (a year), e.g. 24.");

    private static async Task Steps(CliSession s, CliArgs a)
    {
        a.Allow("delay");
        var reference = a.Word(0, "which task");
        _ = a.Word(1, "the steps");
        TimeSpan? delay = a.Value("delay") is { } text ? StepDelay(text, "--delay") : null;
        var item = await s.Change((b, now) =>
        {
            var parent = TaskResolver.Resolve(b, reference, i => !i.IsDone);
            b.AddSteps(parent.Id, a.Words.Skip(1), delay, s.Actor, now);
            return parent;
        }).ConfigureAwait(false);
        await s.Print(item, (o, i) => o.Changed($"Added {(a.Words.Count - 1).ToString(CultureInfo.InvariantCulture)} steps to", i)).ConfigureAwait(false);
    }

    private static async Task Attach(CliSession s, CliArgs a)
    {
        a.Allow();
        var reference = a.Word(0, "which task");
        var path = s.FullPath(a.WordsFrom(1, "which file"));
        if (!File.Exists(path))
        {
            throw new ArgumentException($"There is no file {path}.");
        }

        var id = await s.Read(b => TaskResolver.Resolve(b, reference).Id).ConfigureAwait(false);
        Attachment attachment;
        await using (var stream = File.OpenRead(path))
        {
            attachment = await s.Links.Vault.AddAttachmentAsync(id, Path.GetFileName(path), stream, s.Actor).ConfigureAwait(false);
        }

        await s.SaveVersion().ConfigureAwait(false);
        var dto = await s.Read(b => Wire.Attachment(b.Get(id), attachment)).ConfigureAwait(false);
        var title = await s.Read(b => b.Get(id).Title).ConfigureAwait(false);
        await s.Print(dto, (o, d) => o.Line($"Attached {d.FileName} to {TaskResolver.ShortId(id)} \"{title}\"")).ConfigureAwait(false);
    }

    private static async Task History(CliSession s, CliArgs a)
    {
        a.Allow("text", "limit");
        if (a.Has("no-history"))
        {
            throw new CliUsageException("History is off for this command (--no-history); run it without that option.");
        }

        if (a.Words.Count == 0)
        {
            if (a.Has("text"))
            {
                throw new CliUsageException("--text needs a task: ot history <task> --text <version>.");
            }

            // The whole tasks folder: every saved version, and folders that are git repositories of their own.
            await s.Read(_ => 0).ConfigureAwait(false);
            var log = await s.History.Required.LogAsync(null, Limit(a, 100)).ConfigureAwait(false);
            var skipped = s.History.Required.Skipped;
            await s.Print(new { versions = log, notInHistory = skipped }, (o, v) =>
            {
                o.Versions(v.versions);
                foreach (var folder in v.notInHistory)
                {
                    o.Line($"Not in history (a git repository of its own): {folder}");
                }
            }).ConfigureAwait(false);
            return;
        }

        var reference = a.WordsFrom(0, "which task");
        var id = await s.Read(b => TaskResolver.Resolve(b, reference).Id).ConfigureAwait(false);
        if (a.Value("text") is { } version)
        {
            var text = await s.History.Required.TaskVersionAsync(id, version).ConfigureAwait(false)
                ?? throw new ArgumentException("This task didn't exist in that version.");
            await s.Print(new { version, text }, (o, v) => o.Raw(v.text)).ConfigureAwait(false);
            return;
        }

        var versions = await s.History.Required.TaskHistoryAsync(id).ConfigureAwait(false);
        await s.Print(versions, (o, v) => o.Versions(v)).ConfigureAwait(false);
    }

    private static async Task Restore(CliSession s, CliArgs a)
    {
        a.Allow();
        var reference = a.Word(0, "which task");
        var version = a.Word(1, "which version (from ot history)");
        var id = await s.Read(b => TaskResolver.Resolve(b, reference).Id).ConfigureAwait(false);
        await s.History.Required.RestoreTaskAsync(id, version, s.Actor).ConfigureAwait(false);
        await s.SaveVersion().ConfigureAwait(false);
        var item = await s.Item(id).ConfigureAwait(false);
        await s.Print(item, (o, i) => o.Changed("Restored", i)).ConfigureAwait(false);
    }

    private static async Task Labels(CliSession s, CliArgs a)
    {
        var verb = a.Words.Count > 0 ? a.Words[0].ToLowerInvariant() : "list";
        if (verb == "add")
        {
            a.Allow("color");
        }
        else
        {
            a.Allow();
        }

        Func<TaskBoard, DateTimeOffset, object?>? change = verb switch
        {
            "list" => null,
            "add" => (b, now) => b.DefineLabel(a.WordsFrom(1, "the label's name"), Color(a.Value("color")), s.Actor, now),
            "rename" => (b, now) => RenameLabel(b, a.Word(1, "which label"), a.WordsFrom(2, "the new name"), null, s.Actor, now),
            "color" or "colour" => (b, now) => RenameLabel(b, a.Word(1, "which label"), null, a.Word(2, "the color (#rrggbb)"), s.Actor, now),
            "delete" or "remove" => throw new CliUsageException("Labels are deleted only in the app, by you. ot label <task> -name takes one off a task."),
            _ => throw new CliUsageException("ot labels takes add, rename or color. See ot help labels."),
        };
        if (change is not null)
        {
            await s.Store.UpdateAsync(b => change(b, s.Now)).ConfigureAwait(false);
            await s.SaveVersion().ConfigureAwait(false);
        }

        var labels = await s.Read(Wire.Labels).ConfigureAwait(false);
        await s.Print(labels, (o, l) => o.Labels(l)).ConfigureAwait(false);
    }

    private static object? RenameLabel(TaskBoard b, string name, string? newName, string? color, Actor actor, DateTimeOffset now)
    {
        var current = b.FindLabel(name) ?? throw new ArgumentException($"There's no label \"{name}\". Labels: {string.Join(", ", b.Labels.Select(l => l.Name))}.");
        b.UpdateLabel(current.Name, newName ?? current.Name, color ?? current.Color, actor, now);
        return null;
    }

    private static async Task Vault(CliSession s, CliArgs a)
    {
        a.Allow();
        switch (a.Words.Count > 0 ? a.Words[0].ToLowerInvariant() : null)
        {
            case null:
                await s.Read(_ => 0).ConfigureAwait(false);
                var root = s.Links.Vault.RootPath;
                var info = new VaultDto(root, s.Links.Vault.Problems, VaultBoardStore.Guide, ObsidianVaults.OpenUrl(root));
                await s.Print(info, (o, v) => o.Vault(v)).ConfigureAwait(false);
                break;
            case "guide":
                await s.Context.Out.WriteAsync(VaultBoardStore.Guide).ConfigureAwait(false);
                break;
            case "obsidian":
                var vaults = ObsidianVaults.Discover();
                await s.Print(vaults, (o, v) => o.ObsidianVaults(v)).ConfigureAwait(false);
                break;
            case "use":
                if (s.Actor.Kind == ActorKind.Agent)
                {
                    // Like approving hooks: only the person at the computer moves where the tasks live.
                    throw new InvalidOperationException("Only you can change where your tasks live: do it in the app (Tasks folder in the sidebar's menu, or Settings).");
                }

                var target = a.WordsFrom(1, "which folder or Obsidian vault");
                var obsidian = ObsidianVaults.Discover().FirstOrDefault(v => string.Equals(v.Name, target, StringComparison.OrdinalIgnoreCase));
                var folder = obsidian is not null ? ObsidianVaults.TaskFolderIn(obsidian) : s.FullPath(target);
                s.Settings.SetVaultPath(folder);
                var result = new VaultSettingDto(folder, RestartRequired: true);
                await s.Print(result, (o, r) => o.Line($"Tasks folder is now {r.Path}. Restart One Thing to switch.")).ConfigureAwait(false);
                break;
            default:
                throw new CliUsageException("ot vault takes guide, obsidian, or use <folder>.");
        }
    }

    private static async Task Hooks(CliSession s, CliArgs a)
    {
        a.Allow("powershell", "bash", "url", "name", "timeout", "group", "tag", "label", "actor", "event");
        var store = new OneThing.Server.Hooks.HookStore(s.Options.DataDirectory);

        // ot run by an agent (--as / ONETHING_AGENT): what it adds or changes waits for the person's approval, like in the app.
        var author = s.Actor.Kind == ActorKind.Agent ? new OneThing.Server.Hooks.HookAuthor(s.Actor.Name ?? "agent", Trusted: false) : OneThing.Server.Hooks.HookAuthor.You;
        var verb = a.Words.Count > 0 ? a.Words[0].ToLowerInvariant() : "list";
        string Id() => a.Word(1, "which hook (its id, as ot hooks lists it)");
        switch (verb)
        {
            case "list":
                await s.Print(store.List(), (o, list) => o.Hooks(list, store.FilePath, store.Read().Problem)).ConfigureAwait(false);
                break;
            case "path":
                await s.Print(store.FilePath, (o, p) => o.Line(p)).ConfigureAwait(false);
                break;
            case "add":
                var added = store.Add(a.Word(1, "the event (taskCompleted, reminderDue… or *)"), Fill(new OneThing.Server.Hooks.HookSpec(), a), author, s.Now);
                await s.Print(added, (o, h) => o.Line(h.Hook.NeedsApproval == true
                    ? $"Added hook {h.Id} for {h.Event}. It waits for approval: ot hooks list shows it (or Settings › Hooks)."
                    : $"Added hook {h.Id} for {h.Event}.")).ConfigureAwait(false);
                break;
            case "edit":
                var current = store.Get(Id());
                var changed = store.Update(current.Id, a.Value("event"), Fill(current.Hook, a), author, s.Now);
                await s.Print(changed, (o, h) => o.Line(h.Hook.NeedsApproval == true ? $"Changed hook {h.Id}. It still waits for approval (ot hooks list)." : $"Changed hook {h.Id}.")).ConfigureAwait(false);
                break;
            case "enable" or "disable":
                var toggled = store.SetEnabled(Id(), verb == "enable", author);
                await s.Print(toggled, (o, h) => o.Line($"Hook {h.Id} is {h.State}.")).ConfigureAwait(false);
                break;
            case "approve":
                // Approves the version you looked at (ot hooks list shows it): a change made since then isn't approved.
                var pending = store.Get(Id());
                if (a.Words.Count < 3)
                {
                    var shown = new StringWriter(CultureInfo.InvariantCulture);
                    new CliOutput(shown, s.Zone).HookDetails(pending);
                    throw new CliUsageException($"Check what it does, then approve that version:\n{shown}ot hooks approve {pending.Id} {pending.Version[..OneThing.Server.Hooks.HookStore.MinVersionPrefix]}");
                }

                var approved = store.Approve(pending.Id, a.Words[2], author);
                await s.Print(approved, (o, h) => o.Line($"Hook {h.Id} is {h.State}.")).ConfigureAwait(false);
                break;
            case "reject":
                // "Don't allow": only the person at the computer, and only the version they looked at.
                OwnerOnly(s, "Only you can turn down a hook: run ot hooks reject without --as (or ONETHING_AGENT), or in Settings › Hooks of the One Thing app.");
                var waiting = store.Get(Id());
                if (!store.Reject(waiting.Id, a.Word(2, "which version you looked at (ot hooks list shows it)")))
                {
                    throw new InvalidOperationException("This hook was allowed or changed meanwhile: it was kept. Look at it again: ot hooks list.");
                }

                await s.Print(new { rejected = waiting.Id }, (o, _) => o.Line($"Turned down hook {waiting.Id}: it's gone.")).ConfigureAwait(false);
                break;
            case "remove" or "delete":
                var id = Id();
                store.Remove(id, author);
                await s.Print(new { removed = id }, (o, _) => o.Line($"Removed hook {id}.")).ConfigureAwait(false);
                break;
            case "test":
                var runs = await TestHooks(s, store, a.Words.Count > 1 ? a.Words[1] : "test").ConfigureAwait(false);
                await s.Print(runs, (o, r) => o.HookRuns(r)).ConfigureAwait(false);
                break;
            default:
                throw new CliUsageException("ot hooks takes list, add, edit, enable, disable, approve, reject, remove, test or path.");
        }
    }

    /// <summary>A hook with what was given on the command line (the rest as it was).</summary>
    private static OneThing.Server.Hooks.HookSpec Fill(OneThing.Server.Hooks.HookSpec hook, CliArgs a)
    {
        var url = a.Value("url");
        var match = hook.Match ?? new OneThing.Server.Hooks.HookMatch();
        return new OneThing.Server.Hooks.HookSpec
        {
            Type = url is not null ? "http" : a.Value("powershell") is not null || a.Value("bash") is not null ? "command" : hook.Type,
            Powershell = a.Value("powershell") ?? hook.Powershell,
            Bash = a.Value("bash") ?? hook.Bash,
            Url = url ?? hook.Url,
            Headers = hook.Headers,
            Cwd = hook.Cwd,
            Env = hook.Env,
            Name = a.Value("name") ?? hook.Name,
            TimeoutSec = a.Value("timeout") is { } t ? (int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? seconds : throw new CliUsageException("--timeout is a number of seconds.")) : hook.TimeoutSec,
            Match = new OneThing.Server.Hooks.HookMatch
            {
                Groups = a.Values("group").Count > 0 ? [.. a.Values("group")] : match.Groups,
                Tags = a.Values("tag").Count > 0 ? [.. a.Values("tag")] : match.Tags,
                Labels = a.Values("label").Count > 0 ? [.. a.Values("label")] : match.Labels,
                Actors = a.Values("actor").Count > 0 ? [.. a.Values("actor")] : match.Actors,
            },
        };
    }

    /// <summary>Runs the hooks for <paramref name="eventName"/> now on this computer, with an example task.</summary>
    private static async Task<IReadOnlyList<OneThing.Server.Hooks.HookRun>> TestHooks(CliSession s, OneThing.Server.Hooks.HookStore store, string eventName)
    {
        if (!OneThing.Server.Hooks.EventNames.All.ContainsKey(eventName))
        {
            throw new CliUsageException($"\"{eventName}\" isn't an event.");
        }

        var group = await s.Read(b => b.Groups[0].Name).ConfigureAwait(false);
        var task = new OneThing.Server.Hooks.EventTask(Guid.Empty, "Example task", ["Example task"], group, [], [], "normal", null, eventName == "taskCompleted", null, null);
        var e = new OneThing.Server.Hooks.TaskEvent(OneThing.Server.Hooks.TaskEvent.CurrentVersion, $"test-{Guid.NewGuid():N}"[..17], eventName, s.Now, new OneThing.Server.Hooks.EventActor("user", null), "A test from ot hooks test", task);
        using var service = new OneThing.Server.Hooks.HookService(s.Store, new ServerEvents(), store, s.Options, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<OneThing.Server.Hooks.HookService>.Instance);
        return await service.RunAsync(e).ConfigureAwait(false);
    }
}
