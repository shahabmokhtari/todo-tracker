using System.Globalization;
using System.Text.RegularExpressions;
using OneThing.Core;
using OneThing.Server;
using OneThing.Server.Plugins;

namespace OneThing.Cli;

/// <summary>
/// Reminders, Do now order, time, the focus timer, the plan, rich HTML, attachments, export, settings and extras:
/// the rest of what the app does. Deleting stays in the apps (a shell agent runs ot too, and ot can't tell).
/// </summary>
internal static partial class CliCommands
{
    /// <summary>The words ot answers with "it doesn't delete" instead of "no such command".</summary>
    internal static readonly IReadOnlySet<string> DeleteWords = new HashSet<string>(StringComparer.Ordinal) { "delete", "del", "rm", "remove", "trash" };

    internal const string NoDelete = "ot doesn't delete: finish a task (ot done) or put it away (ot archive). Deleting is for you, in the One Thing app.";

    /// <summary>
    /// Things only the person at the computer does (like approving a hook). ot without --as (or ONETHING_AGENT) is
    /// that person; with it, an agent.
    /// </summary>
    private static void OwnerOnly(CliSession s, string message)
    {
        if (s.Actor.Kind == ActorKind.Agent)
        {
            throw new UnauthorizedAccessException(message);
        }
    }

    /// <summary>One of a task's notes, reminders or time entries: by id (or its first characters), or <c>last</c>.</summary>
    private static T Pick<T>(IEnumerable<T> items, Func<T, Guid> id, Func<T, DateTimeOffset> at, string which, string what)
    {
        var list = items.ToList();
        var text = which.Trim();
        if (text.Equals("last", StringComparison.OrdinalIgnoreCase))
        {
            return list.Count > 0 ? list.MaxBy(at)! : throw new ArgumentException($"This task has no {what} yet.");
        }

        var hex = text.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        var found = list.Where(i => id(i).ToString("N").StartsWith(hex, StringComparison.Ordinal)).ToList();
        return found.Count switch
        {
            1 => found[0],
            0 => throw new ArgumentException($"This task has no {what} {text}. ot show <task> --json lists them (or say last)."),
            _ => throw new ArgumentException($"\"{text}\" is the start of {found.Count.ToString(CultureInfo.InvariantCulture)} {what}s: give more of the id."),
        };
    }

    private static bool OnOff(string text, string option) => text.Trim().ToLowerInvariant() switch
    {
        "on" or "yes" or "true" or "1" => true,
        "off" or "no" or "false" or "0" => false,
        _ => throw new CliUsageException($"{option} is on or off."),
    };

    private static async Task Unarchive(CliSession s, CliArgs a)
    {
        a.Allow();
        var reference = a.WordsFrom(0, "which task");
        var item = await s.Change((b, now) =>
        {
            var found = TaskResolver.Resolve(b, reference, i => i.IsArchived);
            b.Unarchive(found.Id, s.Actor, now);
            return found;
        }).ConfigureAwait(false);
        await s.Print(item, (o, i) => o.Changed("Back from the archive:", i)).ConfigureAwait(false);
    }

    private static async Task Remind(CliSession s, CliArgs a)
    {
        a.Allow("message", "dismiss");
        var reference = a.Word(0, "which task");
        var dismiss = a.Value("dismiss");
        if (dismiss is not null && (a.Words.Count > 1 || a.Has("message")))
        {
            throw new CliUsageException("--dismiss takes a reminder (or all), and no time or message.");
        }

        var item = await s.Change((b, now) =>
        {
            var found = TaskResolver.Resolve(b, reference);
            if (dismiss is null)
            {
                b.AddReminder(found.Id, CliTime.Defer(a.WordsFrom(1, "when (e.g. 2h, tomorrow 9:00, fri 14:30)"), now, s.Zone), a.Value("message"), s.Actor, now);
            }
            else if (dismiss.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var r in found.Reminders.Where(r => r.DismissedAt is null).ToList())
                {
                    b.DismissReminder(found.Id, r.Id, s.Actor, now);
                }
            }
            else
            {
                b.DismissReminder(found.Id, Pick(found.Reminders, r => r.Id, r => r.DueAt, dismiss, "reminder").Id, s.Actor, now);
            }

            return found;
        }).ConfigureAwait(false);
        await s.Print(item, (o, i) => o.Changed(dismiss is null ? "Reminder on" : "Dismissed on", i)).ConfigureAwait(false);
    }

    private static async Task Order(CliSession s, CliArgs a)
    {
        a.Allow();
        _ = a.Word(0, "the tasks, in the order you want them");
        await s.Store.UpdateAsync(b =>
        {
            b.ArrangeNow(a.Words.Select(r => TaskResolver.Resolve(b, r, i => !i.IsDone).Id).ToList(), s.Actor, s.Now);
            return true;
        }).ConfigureAwait(false);
        await s.SaveVersion().ConfigureAwait(false);
        var dashboard = await s.Read(b => Wire.Dashboard(b, s.Now, null, 5, s.Links)).ConfigureAwait(false);
        await s.Print(dashboard, (o, d) => o.Dashboard(d)).ConfigureAwait(false);
    }

    private static async Task TagList(CliSession s, CliArgs a)
    {
        a.Allow();
        var tags = await s.Read(ApiEndpoints.Tags).ConfigureAwait(false);
        await s.Print(tags, (o, t) => o.Tags(t)).ConfigureAwait(false);
    }

    // ---- Time and the focus timer -------------------------------------------------------------------------------

    private static async Task Log(CliSession s, CliArgs a)
    {
        a.Allow("at", "edit");
        var reference = a.Word(0, "which task");
        var duration = Duration(a.Word(1, "how long (45m, 1h30m or minutes)"));
        if (a.Words.Count > 2)
        {
            throw new CliUsageException("Say the task, then how long: ot log <task> 45m (quote a title with spaces).");
        }

        var entry = await s.Store.UpdateAsync(b =>
        {
            var now = s.Now;
            var item = TaskResolver.Resolve(b, reference);
            DateTimeOffset? at = a.Value("at") is { } text ? CliTime.When(text, now, s.Zone) : null;
            if (a.Value("edit") is { } which)
            {
                var old = Pick(item.TimeEntries.Where(e => e.End is not null), e => e.Id, e => e.Start, which, "time entry");
                var start = at ?? old.Start;
                b.UpdateTime(item.Id, old.Id, start, start + duration, s.Actor, now);
                return Wire.TimeEntry(item, item.TimeEntries.First(e => e.Id == old.Id), now);
            }

            var begin = at ?? now - duration;
            return Wire.TimeEntry(item, b.AddTime(item.Id, begin, begin + duration, s.Actor, now), now);
        }).ConfigureAwait(false);
        await s.SaveVersion().ConfigureAwait(false);
        await s.Print(entry, (o, e) => o.Line($"{(a.Has("edit") ? "Changed to" : "Logged")} {TaskMarkdownDuration(TimeSpan.FromSeconds(e.Seconds))} from {o.LocalTime(e.Start)}")).ConfigureAwait(false);
    }

    /// <summary>45m, 1h30m, 1.5h, 2h, or a number of minutes.</summary>
    internal static TimeSpan Duration(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        TimeSpan? span = null;
        if (int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            span = TimeSpan.FromMinutes(minutes);
        }
        else if (DurationPattern().Match(t) is { Success: true } m && (m.Groups[1].Success || m.Groups[2].Success))
        {
            var hours = m.Groups[1].Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            var mins = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            span = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(mins);
        }

        return span is { } d && d > TimeSpan.Zero && d <= TaskBoard.MaxTimeEntry
            ? d
            : throw new CliUsageException($"\"{text}\" isn't a length of time: say 45m, 1h30m, 1.5h or minutes (at most 24 hours).");
    }

    [GeneratedRegex(@"^(?:(\d+(?:\.\d+)?)h)?\s*(?:(\d+)m(?:in)?)?$")]
    private static partial Regex DurationPattern();

    private static async Task Focus(CliSession s, CliArgs a)
    {
        a.Allow();
        var verb = a.Words.Count > 0 ? a.Words[0].ToLowerInvariant() : "status";
        var task = a.Words.Count > 1 ? a.WordsFrom(1, "which task") : null;
        if (task is not null && verb is not ("start" or "next"))
        {
            throw new CliUsageException($"ot focus {verb} takes no task.");
        }

        PomodoroDto focus;
        if (verb == "status")
        {
            focus = await s.Read(b => Wire.Pomodoro(b, s.Now)).ConfigureAwait(false);
        }
        else
        {
            focus = await s.Store.UpdateAsync(b =>
            {
                var now = s.Now;
                Guid? id = task is null ? null : TaskResolver.Resolve(b, task, i => !i.IsDone).Id;
                switch (verb)
                {
                    case "start":
                        b.StartFocus(id, s.Actor, now);
                        break;
                    case "pause":
                        b.PauseFocus(s.Actor, now);
                        break;
                    case "resume":
                        b.ResumeFocus(s.Actor, now);
                        break;
                    case "skip":
                        b.SkipFocus(s.Actor, now);
                        break;
                    case "reset" or "stop":
                        b.ResetFocus(s.Actor, now);
                        break;
                    case "next":
                        b.StartNextFocus(id ?? Agenda.Build(b, now, recentNoteCount: 0).Focus?.Item.Id, s.Actor, now);
                        break;
                    default:
                        throw new CliUsageException("ot focus takes start, pause, resume, skip, reset or next.");
                }

                return Wire.Pomodoro(b, now);
            }).ConfigureAwait(false);
            await s.SaveVersion().ConfigureAwait(false);
        }

        await s.Print(focus, (o, f) => o.Focus(f)).ConfigureAwait(false);
    }

    // ---- The plan -----------------------------------------------------------------------------------------------

    private static async Task Plan(CliSession s, CliArgs a)
    {
        string[] settingOptions = ["view", "hours", "work-hours", "work-days", "week-start"];
        var first = a.Words.Count > 0 ? a.Words[0] : null;
        if (first is null)
        {
            a.Allow("from", "days", "group");
            int? days = a.Value("days") is { } text
                ? int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= PlanDays.MaxDays ? n : throw new CliUsageException($"--days is 1 to {PlanDays.MaxDays.ToString(CultureInfo.InvariantCulture)}.")
                : null;
            var plan = await s.Read(b => PlanEndpoints.Build(b, a.Value("from"), days, Group(b, a.Value("group")), s.Now, s.Zone, s.Settings)).ConfigureAwait(false);
            await s.Print(plan, (o, p) => o.Plan(p)).ConfigureAwait(false);
            return;
        }

        if (first.Equals("settings", StringComparison.OrdinalIgnoreCase) && a.Words.Count == 1)
        {
            a.Allow([.. settingOptions, "group"]);
            var key = PlanEndpoints.Key(await s.Read(b => Group(b, a.Value("group"))).ConfigureAwait(false));
            var settings = s.Settings.PlanFor(key);
            if (settingOptions.Any(a.Has))
            {
                var (start, end) = a.Value("work-hours") is { } hours ? WorkHours(hours) : (settings.WorkStart, settings.WorkEnd);
                settings = s.Settings.SetPlan(key, settings with
                {
                    View = a.Value("view")?.ToLowerInvariant() ?? settings.View,
                    Hours = a.Value("hours")?.ToLowerInvariant() ?? settings.Hours,
                    WorkStart = start,
                    WorkEnd = end,
                    WorkDays = a.Value("work-days") is { } workDays ? WorkDays(workDays) : settings.WorkDays,
                    WeekStart = a.Value("week-start") is { } weekStart ? WeekStart(weekStart) : settings.WeekStart,
                });
            }

            await s.Print(settings, (o, p) => o.PlanSettings(p)).ConfigureAwait(false);
            return;
        }

        a.Allow("minutes");
        var reference = a.Word(0, "which task");
        var when = a.WordsFrom(1, "when (e.g. tomorrow 9:00, 2026-01-05T14:00) or off");
        int? minutes = a.Value("minutes") is { } m
            ? int.TryParse(m, NumberStyles.None, CultureInfo.InvariantCulture, out var length) ? length : throw new CliUsageException("--minutes is a number of minutes, 5 to 720.")
            : null;
        var off = when.Equals("off", StringComparison.OrdinalIgnoreCase);
        var item = await s.Change((b, now) =>
        {
            var found = TaskResolver.Resolve(b, reference, i => !i.IsDone);
            if (off)
            {
                b.Unplan(found.Id, s.Actor, now);
            }
            else
            {
                b.Plan(found.Id, CliTime.When(when, now, s.Zone), minutes, s.Actor, now);
            }

            return found;
        }).ConfigureAwait(false);
        await s.Print(item, (o, i) => o.Changed(off ? "Off the plan:" : $"Planned at {o.LocalTime(i.PlannedAt!.Value)} for {i.PlannedMinutes?.ToString(CultureInfo.InvariantCulture)} min:", i)).ConfigureAwait(false);
    }

    /// <summary>9:00-17:00 as minutes after midnight.</summary>
    private static (int Start, int End) WorkHours(string text)
    {
        var parts = text.Split('-', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && Clock(parts[0]) is { } start && Clock(parts[1]) is { } end
            ? (start, end)
            : throw new CliUsageException("--work-hours is like 9:00-17:00.");

        static int? Clock(string t) => TimeOnly.TryParseExact(t, ["H:mm", "HH:mm", "H"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.Hour * 60 + time.Minute
            : t is "24:00" or "24" ? 24 * 60 : null;
    }

    /// <summary>1-5, 1,2,3,4 or 6,7 (1 = Monday … 7 = Sunday).</summary>
    private static List<int> WorkDays(string text)
    {
        var days = new List<int>();
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var range = part.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length is 1 or 2 && range.All(r => int.TryParse(r, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            {
                var from = int.Parse(range[0], CultureInfo.InvariantCulture);
                var to = int.Parse(range[^1], CultureInfo.InvariantCulture);
                days.AddRange(Enumerable.Range(from, Math.Max(0, to - from + 1)));
            }
            else
            {
                throw new CliUsageException("--work-days is like 1-5 or 1,2,3,4 (1 = Monday … 7 = Sunday).");
            }
        }

        return days;
    }

    private static int WeekStart(string text) => text.Trim().ToLowerInvariant() switch
    {
        "1" or "mon" or "monday" => 1,
        "7" or "sun" or "sunday" => 7,
        _ => throw new CliUsageException("--week-start is mon or sun."),
    };

    // ---- Rich HTML, attachments, export ---------------------------------------------------------------------------

    private static async Task Rich(CliSession s, CliArgs a)
    {
        a.Allow();
        var reference = a.Word(0, "which task");
        var source = a.Words.Count > 1 ? a.WordsFrom(1, "the file") : null;
        var id = await s.Read(b => TaskResolver.Resolve(b, reference).Id).ConfigureAwait(false);
        if (source is null)
        {
            var html = await s.Links.Vault.ReadRichAsync(id).ConfigureAwait(false)
                ?? throw new ArgumentException("This task has no rich version. Save one: ot rich <task> <file.html>");
            await s.Print(new { html }, (o, r) => o.Raw(r.html)).ConfigureAwait(false);
            return;
        }

        string? content = source.Equals("none", StringComparison.OrdinalIgnoreCase) ? null
            : source == "-" ? await s.Context.In.ReadToEndAsync().ConfigureAwait(false)
            : File.Exists(s.FullPath(source)) ? await File.ReadAllTextAsync(s.FullPath(source)).ConfigureAwait(false)
            : throw new ArgumentException($"There is no file {s.FullPath(source)}.");
        var clean = string.IsNullOrWhiteSpace(content) ? null : content;
        await s.Links.Vault.WriteRichAsync(id, clean, s.Actor).ConfigureAwait(false);
        await s.SaveVersion().ConfigureAwait(false);
        await s.Print(new RichDto(clean is not null), (o, r) => o.Line(r.HasRich ? "Saved the rich version" : "Removed the rich version")).ConfigureAwait(false);
    }

    private static async Task AttachmentSave(CliSession s, CliArgs a)
    {
        a.Allow("to");
        var reference = a.Word(0, "which task");
        var which = a.WordsFrom(1, "which attachment (its file name or id; ot show lists them)");
        var (itemId, attachment) = await s.Read(b =>
        {
            var item = TaskResolver.Resolve(b, reference);
            var exact = item.Attachments.Where(x => string.Equals(x.FileName, which, StringComparison.OrdinalIgnoreCase)).ToList();
            var found = exact.Count == 1 ? exact[0]
                : item.Attachments.Where(x => x.FileName.Contains(which, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: 1 } named ? named[0]
                : Pick(item.Attachments, x => x.Id, x => x.AddedAt, which, "attachment");
            return (item.Id, found);
        }).ConfigureAwait(false);
        var (full, name) = await s.Links.Vault.GetAttachmentFileAsync(itemId, attachment.Id).ConfigureAwait(false);
        var to = a.Value("to");
        if (to == "-")
        {
            await s.Context.Out.WriteAsync(await File.ReadAllTextAsync(full).ConfigureAwait(false)).ConfigureAwait(false);
            return;
        }

        var target = s.FullPath(to ?? name);
        if (Directory.Exists(target))
        {
            target = Path.Combine(target, name);
        }

        if (File.Exists(target))
        {
            throw new ArgumentException($"There's already a file {target}: say another place with --to.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(full, target);
        await s.Print(new { itemId, attachmentId = attachment.Id, fileName = name, savedTo = target }, (o, x) => o.Line($"Saved {x.fileName} to {x.savedTo}")).ConfigureAwait(false);
    }

    private static async Task Export(CliSession s, CliArgs a)
    {
        a.Allow("to");
        var json = await s.Read(BoardSerializer.Serialize).ConfigureAwait(false);
        if (a.Value("to") is { } to)
        {
            var path = s.FullPath(to);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            await s.Print(new { exported = path }, (o, x) => o.Line($"Exported to {x.exported}")).ConfigureAwait(false);
            return;
        }

        await s.Context.Out.WriteLineAsync(json).ConfigureAwait(false);
    }

    // ---- Settings and extras ------------------------------------------------------------------------------------

    private static async Task SettingsCommand(CliSession s, CliArgs a)
    {
        a.Allow();
        switch (a.Words.Count > 0 ? a.Words[0].ToLowerInvariant() : null)
        {
            case null:
                break;
            case "theme":
                s.Settings.SetTheme(a.Word(1, "the theme (system, light or dark)"));
                break;
            case "teams":
                OwnerOnly(s, "Only you can choose where reminders are posted: run ot settings teams without --as (or ONETHING_AGENT), or in Settings of the One Thing app.");
                var url = a.Word(1, "the Teams webhook (https://…), or off");
                s.Settings.SetTeamsWebhook(url.Equals("off", StringComparison.OrdinalIgnoreCase) || url.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : url);
                break;
            default:
                throw new CliUsageException("ot settings takes theme or teams. See ot help settings.");
        }

        await s.Print(s.Settings.ToDto(), (o, d) => o.Settings(d)).ConfigureAwait(false);
    }

    private static async Task Plugins(CliSession s, CliArgs a)
    {
        a.Allow();
        var settings = new PluginSettings(s.Options.DataDirectory);
        var verb = a.Words.Count > 0 ? a.Words[0].ToLowerInvariant() : null;
        if (verb is "on" or "off")
        {
            OwnerOnly(s, "Only you can switch extras on or off: run ot plugins without --as (or ONETHING_AGENT), or in Settings › Extras of the One Thing app.");
            var id = a.Word(1, "which extra (its id, as ot plugins lists it)");
            var plugin = PluginHost.BuiltIn.FirstOrDefault(p => string.Equals(p.Info.Id, id, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"There's no extra \"{id}\". ot plugins lists them.");
            settings.SetEnabled(plugin.Info.Id, verb == "on");
        }
        else if (verb is not null)
        {
            throw new CliUsageException("ot plugins takes on <id> or off <id>.");
        }

        var list = PluginHost.BuiltIn.Select(p => new PluginDto(p.Info.Id, p.Info.Name, p.Info.Description, settings.IsEnabled(p.Info), null)).ToList();
        await s.Print(list, (o, l) =>
        {
            o.Plugins(l);
            if (verb is not null)
            {
                o.Line("One Thing uses it from its next start (a running app switches an extra off at once from Settings › Extras).");
            }
        }).ConfigureAwait(false);
    }
}
