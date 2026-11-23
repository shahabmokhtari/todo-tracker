using System.Globalization;
using OneThing.Server;

namespace OneThing.Cli;

/// <summary>Text output for the timeline, the plan, the focus timer, settings and the rest.</summary>
internal sealed partial class CliOutput
{
    public void Timeline(IReadOnlyList<TimelineDto> entries)
    {
        if (entries.Count == 0)
        {
            Writer.WriteLine("Nothing has happened yet.");
        }

        foreach (var e in entries)
        {
            Writer.WriteLine($"  {Local(e.At)}  {e.Actor,-12} {e.Summary}");
        }
    }

    /// <summary>Text as it is (a version of a file, rich HTML), ending with a line break.</summary>
    public void Raw(string text)
    {
        Writer.Write(text);
        if (!text.EndsWith('\n'))
        {
            Writer.WriteLine();
        }
    }

    public string LocalTime(DateTimeOffset at) => Local(at);

    public void Tags(IReadOnlyList<TagDto> tags)
    {
        if (tags.Count == 0)
        {
            Writer.WriteLine("No tags on open tasks. Add one: ot tag <task> +name");
        }

        foreach (var t in tags)
        {
            Writer.WriteLine($"  #{t.Name}  ({t.Count.ToString(CultureInfo.InvariantCulture)})");
        }
    }

    public void Focus(PomodoroDto f)
    {
        var phase = f.Phase switch
        {
            "focus" => "Focus",
            "shortBreak" => "Short break",
            "longBreak" => "Long break",
            _ => "Not running",
        };
        if (f.Phase == "idle")
        {
            Writer.WriteLine($"Focus timer: not running ({f.FocusMinutes.ToString(CultureInfo.InvariantCulture)} min sessions). ot focus start <task> starts one.");
            return;
        }

        var left = TimeSpan.FromSeconds(f.RemainingSeconds);
        var on = f.ItemTitle is null ? string.Empty : $" on \"{f.ItemTitle}\"";
        var paused = f.Running ? string.Empty : " (paused)";
        Writer.WriteLine($"{phase}{on}: {((int)left.TotalMinutes).ToString(CultureInfo.InvariantCulture)}:{left.Seconds.ToString("00", CultureInfo.InvariantCulture)} left{paused} · {f.CompletedFocusCount.ToString(CultureInfo.InvariantCulture)} focus session(s) done");
    }

    public void Plan(PlanDto p)
    {
        Writer.WriteLine($"Plan from {p.From} ({p.Days.ToString(CultureInfo.InvariantCulture)} day(s))");
        if (p.Blocks.Count == 0)
        {
            Writer.WriteLine("  Nothing planned. ot plan <task> tomorrow 9:00 plans a block.");
        }

        foreach (var b in p.Blocks.OrderBy(b => b.Start))
        {
            Writer.WriteLine($"  {Local(b.Start)}  {b.Minutes.ToString(CultureInfo.InvariantCulture),4} min  {TaskResolver.ShortId(b.Id)}  {(b.Done ? "[x]" : "[ ]")} {b.Title}");
        }

        foreach (var d in p.Deadlines)
        {
            Writer.WriteLine($"  due {Local(d.Deadline)}  {TaskResolver.ShortId(d.Id)}  {d.Title}");
        }

        if (p.Unplanned.Count > 0)
        {
            Writer.WriteLine($"Not planned ({p.Unplanned.Count.ToString(CultureInfo.InvariantCulture)})");
            foreach (var t in p.Unplanned.Take(10))
            {
                Writer.WriteLine($"  {TaskResolver.ShortId(t.Id)}  {t.Title}");
            }
        }
    }

    public void PlanSettings(PlanSettings p)
    {
        static string Clock(int minutes) => $"{minutes / 60}:{minutes % 60:00}";
        var days = string.Join(",", (p.WorkDays ?? OneThing.Server.PlanSettings.DefaultWorkDays).Select(d => d.ToString(CultureInfo.InvariantCulture)));
        Writer.WriteLine($"Plan: a {p.View}, {(p.Hours == "work" ? "work hours" : "the full day")}; work hours {Clock(p.WorkStart)}-{Clock(p.WorkEnd)}, work days {days}, weeks start on {(p.WeekStart == 7 ? "Sunday" : "Monday")}");
    }

    public void Settings(SettingsDto d)
    {
        Writer.WriteLine($"Theme: {d.Theme}");
        Writer.WriteLine(d.TeamsConfigured ? $"Reminders also go to Teams ({d.TeamsWebhookHost})" : "Reminders don't go to Teams");
    }

    public void Plugins(IReadOnlyList<OneThing.Server.Plugins.PluginDto> plugins)
    {
        foreach (var p in plugins)
        {
            Writer.WriteLine($"  {(p.Enabled ? "on " : "off")}  {p.Id,-20} {p.Name}");
        }
    }
}
