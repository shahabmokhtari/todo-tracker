using System.Globalization;
using System.Text;
using TodoTracker.Core;
using TodoTracker.Core.Vault;
using TodoTracker.Server;

namespace TodoTracker.Cli;

internal sealed record CliCommand(string Name, string Usage, string Summary, string? Details, Func<CliSession, CliArgs, Task> Run);

/// <summary>Every <c>tt</c> command, its help, and what it does.</summary>
internal static class CliCommands
{
    public static readonly IReadOnlyDictionary<string, CliCommand> All = new[]
    {
        new CliCommand("now", "tt [now] [filter] [-g group]", "What to do now, and what is waiting", "filter uses the list syntax, e.g. tt now #release", Now),
        new CliCommand("add", "tt add <title…> [-g group] [--under task] [--details text] [--tag t]… [--label l]… [--due when] [--snooze when]", "Add a task",
            "Quick words in the title: ! high, !! critical, !low, @2h or @tomorrow (defer), due:3d or due:2026-02-01, #tag.\nExample: tt add Renew passport !! due:7d #admin", Add),
        new CliCommand("list", "tt list [filter] [-g group] [--all]", "Find tasks",
            "Words match titles and details; #tag, label:name, group:name, is:open, is:done narrow it. Open tasks unless --all or is:.", List),
        new CliCommand("show", "tt show <task>", "A task with its subtasks, notes and file", null, Show),
        new CliCommand("done", "tt done <task>…", "Mark tasks done", null, Done),
        new CliCommand("reopen", "tt reopen <task>", "Undo done", null, Reopen),
        new CliCommand("note", "tt note <task> <text…|->", "Log progress (\"did X, next Y\"); - reads the text from stdin", null, Note),
        new CliCommand("snooze", "tt snooze <task> <when>", "Defer a task until later (it moves to Waiting and reminds you)", "when: 45m, 2h, 3d, tomorrow, 2026-02-01, 2026-02-01T14:30", Snooze),
        new CliCommand("edit", "tt edit <task> [--title t] [--details d] [--priority low|normal|high|critical] [--due when|none]", "Change a task", null, Edit),
        new CliCommand("tag", "tt tag <task> [+]tag… -tag…", "Add or remove tags", null, (s, a) => Tags(s, a, labels: false)),
        new CliCommand("label", "tt label <task> [+]label… -label…", "Add or remove labels (quote names with spaces)", null, (s, a) => Tags(s, a, labels: true)),
        new CliCommand("move", "tt move <task> (--under task | --top | -g group) [--index n]", "Put a task under another, at the top level, or in another group", null, Move),
        new CliCommand("steps", "tt steps <task> <step>… [--delay hours]", "Add ordered steps; finishing one unlocks the next after the delay", null, Steps),
        new CliCommand("attach", "tt attach <task> <file>", "Copy a file into the tasks folder and link it", null, Attach),
        new CliCommand("history", "tt history <task>", "Saved versions of a task, newest first", null, History),
        new CliCommand("restore", "tt restore <task> <version>", "Put a task back the way it was in a version", null, Restore),
        new CliCommand("groups", "tt groups", "Groups (tabs) with counts", null, Groups),
        new CliCommand("labels", "tt labels", "The curated labels", null, Labels),
        new CliCommand("vault", "tt vault [guide | obsidian | use <folder|obsidian vault>]", "Where the task files live, their format, or switch folders", null, Vault),
        new CliCommand("mcp", "tt mcp", "Run the MCP server over stdio (for Claude, Copilot, VS Code…)", "See docs/ai-connectors.md for setup in each app.", (_, _) => Task.CompletedTask),
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

            text.AppendLine().AppendLine("<task> is an id (or its first 6+ characters, as tt prints them) or words from the title.");
            return text.ToString();
        }

        text.AppendLine("tt — Todo Tracker in your terminal and for AI agents").AppendLine().AppendLine("Commands:");
        var width = All.Keys.Max(k => k.Length) + 2;
        foreach (var c in All.Values)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {c.Name.PadRight(width)}{c.Summary}");
        }

        text.AppendLine()
            .AppendLine("Options for every command:")
            .AppendLine("  --json          print JSON (same shapes as the REST API and MCP tools)")
            .AppendLine("  --as <name>     attribute changes to an agent, e.g. --as claude (or set TT_AGENT)")
            .AppendLine("  --vault <dir>   use another tasks folder (default: the app's, or TODOTRACKER_VAULT)")
            .AppendLine("  --data <dir>    use another app data folder (or TODOTRACKER_DATA)")
            .AppendLine("  --no-history    don't save a version for this change")
            .AppendLine()
            .AppendLine("tt help <command> shows details. <task> is an id prefix or words from the title.");
        return text.ToString();
    }

    private static async Task Now(CliSession s, CliArgs a)
    {
        a.Allow("group");
        var filter = a.Words.Count > 0 ? string.Join(' ', a.Words) : null;
        var dashboard = await s.Read(b => Wire.Dashboard(b, s.Now, TodoTools.ResolveGroup(b, a.Value("group")), 5, s.Links, filter)).ConfigureAwait(false);
        await s.Print(dashboard, (o, d) => o.Dashboard(d)).ConfigureAwait(false);
    }

    private static async Task Add(CliSession s, CliArgs a)
    {
        a.Allow("group", "under", "details", "tag", "label", "due", "snooze");
        var text = a.WordsFrom(0, "what to add");
        var item = await s.Change((b, now) =>
        {
            var capture = QuickCaptureParser.Parse(text, now, s.Zone);
            var parent = a.Value("under") is { } under ? TaskResolver.Resolve(b, under, i => !i.IsDone) : null;
            var created = b.AddTask(
                new NewTask(capture.Title)
                {
                    ParentId = parent?.Id,
                    GroupId = TodoTools.ResolveGroup(b, a.Value("group")),
                    Priority = capture.Priority,
                    Details = a.Value("details"),
                    Deadline = a.Value("due") is { } due ? CliTime.Deadline(due, now, s.Zone) : capture.Deadline,
                    Tags = [.. capture.Tags, .. a.Values("tag")],
                    Labels = a.Values("label").Count > 0 ? a.Values("label") : null,
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

        var includeDone = a.Has("all") || filter.Contains("is:", StringComparison.OrdinalIgnoreCase);
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

    private static async Task Done(CliSession s, CliArgs a)
    {
        a.Allow();
        _ = a.Word(0, "which task");
        var done = new List<ItemDto>();
        foreach (var reference in a.Words)
        {
            done.Add(await s.Change((b, now) =>
            {
                var item = TaskResolver.Resolve(b, reference, i => !i.IsDone);
                b.Complete(item.Id, s.Actor, now);
                return item;
            }).ConfigureAwait(false));
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

    private static async Task Note(CliSession s, CliArgs a)
    {
        a.Allow();
        var reference = a.Word(0, "which task");
        var text = a.Words.Count == 2 && a.Words[1] == "-"
            ? (await s.Context.In.ReadToEndAsync().ConfigureAwait(false)).Trim().ReplaceLineEndings("\n")
            : a.WordsFrom(1, "the note");
        NoteDto? note = null;
        await s.Change((b, now) =>
        {
            var item = TaskResolver.Resolve(b, reference);
            note = Wire.Note(item, b.AddNote(item.Id, text, s.Actor, now));
            return item;
        }).ConfigureAwait(false);
        await s.Print(note!, (o, n) => o.Line($"Noted on {TaskResolver.ShortId(n.ItemId)} \"{n.ItemTitle}\"")).ConfigureAwait(false);
    }

    private static async Task Snooze(CliSession s, CliArgs a)
    {
        a.Allow();
        var reference = a.Word(0, "which task");
        var when = a.WordsFrom(1, "when (e.g. 2h, tomorrow)");
        var item = await s.Change((b, now) =>
        {
            var found = TaskResolver.Resolve(b, reference, i => !i.IsDone);
            b.ScheduleNextAction(found.Id, CliTime.Defer(when, now, s.Zone), s.Actor, now, notify: true);
            return found;
        }).ConfigureAwait(false);
        await s.Print(item, (o, i) => o.Changed("Snoozed", i)).ConfigureAwait(false);
    }

    private static async Task Edit(CliSession s, CliArgs a)
    {
        a.Allow("title", "details", "priority", "due");
        var reference = a.WordsFrom(0, "which task");
        if (!a.Has("title") && !a.Has("details") && !a.Has("priority") && !a.Has("due"))
        {
            throw new CliUsageException("Say what to change: --title, --details, --priority, or --due.");
        }

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
                },
                s.Actor,
                now);
            return found;
        }).ConfigureAwait(false);
        await s.Print(item, (o, i) => o.Changed("Updated", i)).ConfigureAwait(false);
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
        a.Allow("under", "top", "group", "index");
        var reference = a.WordsFrom(0, "which task");
        if (!a.Has("under") && !a.Has("top") && !a.Has("group"))
        {
            throw new CliUsageException("Say where: --under <task>, --top, or --group <group>.");
        }

        int? index = a.Value("index") is { } text
            ? int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : throw new CliUsageException("--index must be 0 or more.")
            : null;
        var item = await s.Change((b, now) =>
        {
            var found = TaskResolver.Resolve(b, reference);
            var group = TodoTools.ResolveGroup(b, a.Value("group"));
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

    private static async Task Steps(CliSession s, CliArgs a)
    {
        a.Allow("delay");
        var reference = a.Word(0, "which task");
        _ = a.Word(1, "the steps");
        TimeSpan? delay = a.Value("delay") is { } text
            ? double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) && hours >= 0 ? TimeSpan.FromHours(hours) : throw new CliUsageException("--delay is in hours, e.g. 24.")
            : null;
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
        a.Allow();
        var reference = a.WordsFrom(0, "which task");
        var id = await s.Read(b => TaskResolver.Resolve(b, reference).Id).ConfigureAwait(false);
        var versions = await s.History.Required.TaskHistoryAsync(id).ConfigureAwait(false);
        await s.Print(versions, (o, v) => o.Versions(v)).ConfigureAwait(false);
    }

    private static async Task Restore(CliSession s, CliArgs a)
    {
        a.Allow();
        var reference = a.Word(0, "which task");
        var version = a.Word(1, "which version (from tt history)");
        var id = await s.Read(b => TaskResolver.Resolve(b, reference).Id).ConfigureAwait(false);
        await s.History.Required.RestoreTaskAsync(id, version, s.Actor).ConfigureAwait(false);
        await s.SaveVersion().ConfigureAwait(false);
        var item = await s.Item(id).ConfigureAwait(false);
        await s.Print(item, (o, i) => o.Changed("Restored", i)).ConfigureAwait(false);
    }

    private static async Task Groups(CliSession s, CliArgs a)
    {
        a.Allow();
        var groups = await s.Read(b => Wire.Dashboard(b, s.Now, null, 0).Groups).ConfigureAwait(false);
        await s.Print(groups, (o, g) => o.Groups(g)).ConfigureAwait(false);
    }

    private static async Task Labels(CliSession s, CliArgs a)
    {
        a.Allow();
        var labels = await s.Read(Wire.Labels).ConfigureAwait(false);
        await s.Print(labels, (o, l) => o.Labels(l)).ConfigureAwait(false);
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
                var target = a.WordsFrom(1, "which folder or Obsidian vault");
                var obsidian = ObsidianVaults.Discover().FirstOrDefault(v => string.Equals(v.Name, target, StringComparison.OrdinalIgnoreCase));
                var folder = obsidian is not null ? ObsidianVaults.TaskFolderIn(obsidian) : s.FullPath(target);
                s.Settings.SetVaultPath(folder);
                var result = new VaultSettingDto(folder, RestartRequired: true);
                await s.Print(result, (o, r) => o.Line($"Tasks folder is now {r.Path}. Restart Todo Tracker to switch.")).ConfigureAwait(false);
                break;
            default:
                throw new CliUsageException("tt vault takes guide, obsidian, or use <folder>.");
        }
    }
}
