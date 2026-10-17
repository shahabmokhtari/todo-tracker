using System.Globalization;
using TodoTracker.Core;
using TodoTracker.Core.Vault;
using TodoTracker.Server;

namespace TodoTracker.Cli;

/// <summary>Short, scannable text output. Ids are printed first (8 characters) so they are easy to copy.</summary>
internal sealed class CliOutput(TextWriter output, TimeZoneInfo zone)
{
    public void Line(string text) => output.WriteLine(text);

    public void Dashboard(DashboardDto d)
    {
        if (d.Groups.Count > 1)
        {
            output.WriteLine(string.Join("  ", d.Groups.Select(g => g.Id == d.GroupId ? $"[{g.Name}]" : g.Name)));
        }

        if (d.Focus is null && d.Now.Count == 0 && d.Waiting.Count == 0)
        {
            output.WriteLine(d.Query is null ? "Nothing to do right now. Add something: tt add \"…\"" : $"Nothing matches {d.Query}.");
            return;
        }

        if (d.Focus is { } focus)
        {
            output.WriteLine("Focus");
            Card(focus);
        }

        var rest = d.Now.Where(c => c.Id != d.Focus?.Id).ToList();
        if (rest.Count > 0)
        {
            output.WriteLine(Count("Now", rest.Count));
            rest.ForEach(Card);
        }

        if (d.Waiting.Count > 0)
        {
            output.WriteLine(Count("Waiting", d.Waiting.Count));
            foreach (var card in d.Waiting)
            {
                Card(card);
            }
        }

        foreach (var problem in d.Problems)
        {
            output.WriteLine($"! {problem.Path}: {problem.Message}");
        }
    }

    public void Hits(IReadOnlyList<SearchHitDto> hits, string filter)
    {
        if (hits.Count == 0)
        {
            output.WriteLine(string.IsNullOrWhiteSpace(filter) ? "No open tasks." : $"Nothing matches {filter.Trim()}.");
            return;
        }

        foreach (var h in hits)
        {
            var meta = Meta(h.Priority, h.Deadline, h.State == "waiting" ? h.NextActionAt : null, h.Tags, h.Labels);
            output.WriteLine($"  {TaskResolver.ShortId(h.Id)}  {(h.IsDone ? "[x]" : "[ ]")} {h.Title}{Crumb(h.Breadcrumb)}{meta}");
        }
    }

    public void Item(ItemDto i, string root)
    {
        output.WriteLine($"{i.Title}  ({i.Id})");
        var facts = new List<string> { i.State };
        if (i.Priority != "normal")
        {
            facts.Add(i.Priority);
        }

        if (i.Deadline is { } due)
        {
            facts.Add($"due {When(due)}");
        }

        if (i.Energy is { } energy)
        {
            facts.Add($"energy {energy}/h");
        }

        if (i.State == "waiting" && i.NextActionAt is { } wake)
        {
            facts.Add($"wakes {When(wake)}");
        }

        output.WriteLine("  " + string.Join(" · ", facts));
        if (i.Path.Count > 1)
        {
            output.WriteLine("  in " + string.Join(" › ", i.Path.Take(i.Path.Count - 1)));
        }

        if (i.Tags.Count + i.Labels.Count > 0)
        {
            output.WriteLine("  " + string.Join(' ', i.Tags.Select(t => "#" + t).Concat(i.Labels.Select(l => $"[{l.Name}]"))));
        }

        if (i.File is { } file)
        {
            output.WriteLine("  file: " + Path.GetFullPath(Path.Combine(root, file)));
        }

        if (i.Link is { } link)
        {
            output.WriteLine("  link: " + link);
        }

        if (!string.IsNullOrWhiteSpace(i.Details))
        {
            output.WriteLine();
            foreach (var line in i.Details.Split('\n'))
            {
                output.WriteLine("  " + line.TrimEnd('\r'));
            }
        }

        if (i.Children.Count > 0)
        {
            output.WriteLine();
            output.WriteLine(i.Sequential ? "Steps" : "Subtasks");
            Children(i.Children, 1);
        }

        if (i.Notes.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Notes");
            foreach (var n in i.Notes)
            {
                output.WriteLine($"  {Local(n.At)}  {n.Author}: {n.Text.ReplaceLineEndings(Environment.NewLine + "    ")}");
            }
        }

        if (i.Attachments.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Attachments");
            foreach (var a in i.Attachments)
            {
                output.WriteLine($"  {a.FileName}  ({Size(a.Size)})");
            }
        }
    }

    public void Changed(string verb, ItemDto i, bool withGroup = false, bool withTags = false)
    {
        var text = $"{verb} {TaskResolver.ShortId(i.Id)} \"{i.Title}\"";
        if (withGroup && i.Path.Count > 1)
        {
            text += " under " + string.Join(" › ", i.Path.Take(i.Path.Count - 1));
        }

        if (i.State == "waiting" && i.NextActionAt is { } wake)
        {
            text += $" — waiting until {When(wake)}";
        }

        if (withTags)
        {
            text += ": " + (i.Tags.Count + i.Labels.Count == 0 ? "(none)" : string.Join(' ', i.Tags.Select(t => "#" + t).Concat(i.Labels.Select(l => $"[{l.Name}]"))));
        }

        output.WriteLine(text);
    }

    public void Versions(IReadOnlyList<VaultVersion> versions)
    {
        if (versions.Count == 0)
        {
            output.WriteLine("No saved versions yet.");
            return;
        }

        foreach (var v in versions)
        {
            output.WriteLine($"  {v.Id[..Math.Min(10, v.Id.Length)]}  {Local(v.At)}  {v.Message}");
        }
    }

    public void Groups(IReadOnlyList<GroupDto> groups)
    {
        foreach (var g in groups)
        {
            output.WriteLine($"  {g.Name}  ({g.Now.ToString(CultureInfo.InvariantCulture)} now, {g.Waiting.ToString(CultureInfo.InvariantCulture)} waiting)");
        }
    }

    public void Hooks(IReadOnlyList<TodoTracker.Server.Hooks.HookEntry> hooks, string path, string? problem)
    {
        if (problem is not null)
        {
            output.WriteLine(problem);
        }

        if (hooks.Count == 0)
        {
            output.WriteLine($"No hooks yet ({path}). Add one: tt hooks add taskCompleted --bash 'cat >> ~/done.jsonl' --powershell '$input | Add-Content $HOME\\done.jsonl'");
            return;
        }

        foreach (var h in hooks)
        {
            var state = h.Problem ?? h.State;
            output.WriteLine($"{h.Id}  {h.Event,-16} {state,-15} {h.Hook.Label}{(h.Hook.NeedsApproval == true ? $"  (added by {h.Hook.AddedBy})" : string.Empty)}");

            // A hook waiting for approval shows everything it would do (its name alone could say anything).
            if (h.Hook.NeedsApproval == true)
            {
                HookDetails(h, indent: "    ");
                output.WriteLine($"    Approve it: tt hooks approve {h.Id} {h.Version[..TodoTracker.Server.Hooks.HookStore.MinVersionPrefix]}");
            }
        }

        output.WriteLine();
        output.WriteLine($"File: {path}");
    }

    /// <summary>Everything a hook does: what it runs (or calls), with which variables, where, and for what.</summary>
    public void HookDetails(TodoTracker.Server.Hooks.HookEntry h, string indent = "  ")
    {
        var x = h.Hook;
        void Show(string what, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                output.WriteLine($"{indent}{what}: {value.Replace("\n", "\n" + indent + "  ", StringComparison.Ordinal)}");
            }
        }

        Show("PowerShell", x.Powershell);
        Show("bash", x.Bash);
        Show("Web address", x.Url);
        Show("Headers", x.Headers is { Count: > 0 } headers ? string.Join(", ", headers.Select(p => $"{p.Key}: {p.Value}")) : null);
        Show("Variables", x.Env is { Count: > 0 } env ? string.Join(", ", env.Select(p => $"{p.Key}={p.Value}")) : null);
        Show("Folder", x.Cwd);
        Show("Time limit", x.TimeoutSec is { } t ? $"{t} s" : null);
        Show("Only for", x.Match is { } m ? string.Join("; ", new[] { ("groups", m.Groups), ("tags", m.Tags), ("labels", m.Labels), ("done by", m.Actors) }.Where(p => p.Item2 is { Count: > 0 }).Select(p => $"{p.Item1} {string.Join(", ", p.Item2!)}")) : null);
    }

    public void HookRuns(IReadOnlyList<TodoTracker.Server.Hooks.HookRun> runs)
    {
        if (runs.Count == 0)
        {
            output.WriteLine("No hook runs for that event (none set up, or they're off or waiting for approval).");
        }

        foreach (var r in runs)
        {
            output.WriteLine($"{(r.Ok ? "OK    " : "FAILED")} {r.Hook}{(r.ExitCode is { } code && !r.Ok ? $" (exit {code})" : string.Empty)}");
            if (r.Output is { } text)
            {
                output.WriteLine("  " + text.Replace("\n", "\n  ", StringComparison.Ordinal));
            }
        }
    }

    public void Energy(EnergyDto e)
    {
        var left = e.PerDay - e.PlannedToday;
        output.WriteLine($"Energy today: {e.PlannedToday:0.#} of {e.PerDay} planned{(left < 0 ? $" ({-left:0.#} too much)" : string.Empty)}");
    }

    public void Labels(IReadOnlyList<LabelDto> labels)
    {
        if (labels.Count == 0)
        {
            output.WriteLine("No labels yet. Add one: tt label <task> \"Deep work\"");
        }

        foreach (var l in labels)
        {
            output.WriteLine($"  {l.Name}  {l.Color}");
        }
    }

    public void Vault(VaultDto v)
    {
        output.WriteLine($"Tasks folder: {v.Path}");
        output.WriteLine($"Open in Obsidian: {v.ObsidianUrl}");
        foreach (var p in v.Problems)
        {
            output.WriteLine($"! {p.Path}: {p.Message}");
        }

        output.WriteLine("File format: tt vault guide");
    }

    public void ObsidianVaults(IReadOnlyList<ObsidianVault> vaults)
    {
        if (vaults.Count == 0)
        {
            output.WriteLine("No Obsidian vaults found.");
        }

        foreach (var v in vaults)
        {
            output.WriteLine($"  {v.Name}  {v.Path}");
        }
    }

    private static string Count(string title, int count) => $"{title} ({count.ToString(CultureInfo.InvariantCulture)})";

    private static string Crumb(IReadOnlyList<string> breadcrumb) => breadcrumb.Count > 0 ? "  — " + string.Join(" › ", breadcrumb) : string.Empty;

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes.ToString(CultureInfo.InvariantCulture)} B",
        < 1024 * 1024 => $"{(bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture)} KB",
        _ => $"{(bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture)} MB",
    };

    private void Card(CardDto c)
    {
        var meta = new List<string>();
        if (c.Priority is "high" or "critical")
        {
            meta.Add(c.Priority);
        }

        if (c.StepNumber is { } step)
        {
            meta.Add($"step {step.ToString(CultureInfo.InvariantCulture)}/{c.StepCount?.ToString(CultureInfo.InvariantCulture)}");
        }

        if (c.DeadlineIn is { } dueIn)
        {
            meta.Add(c.IsOverdue ? $"overdue ({dueIn})" : $"due {dueIn}");
        }

        if (c.State == "waiting" && c.WakeIn is { } wake)
        {
            meta.Add($"wakes {wake}");
        }

        meta.AddRange(c.Tags.Select(t => "#" + t));
        meta.AddRange(c.Labels.Select(l => $"[{l.Name}]"));
        output.WriteLine($"  {TaskResolver.ShortId(c.Id)}  {c.Title}{Crumb(c.Breadcrumb)}{(meta.Count > 0 ? "  · " + string.Join(" · ", meta) : string.Empty)}");
    }

    private string Meta(string priority, DateTimeOffset? deadline, DateTimeOffset? wake, IReadOnlyList<string> tags, IReadOnlyList<LabelDto> labels)
    {
        var meta = new List<string>();
        if (priority is "high" or "critical")
        {
            meta.Add(priority);
        }

        if (deadline is { } d)
        {
            meta.Add($"due {When(d)}");
        }

        if (wake is { } w)
        {
            meta.Add($"wakes {When(w)}");
        }

        meta.AddRange(tags.Select(t => "#" + t));
        meta.AddRange(labels.Select(l => $"[{l.Name}]"));
        return meta.Count > 0 ? "  · " + string.Join(" · ", meta) : string.Empty;
    }

    private void Children(IReadOnlyList<ItemDto> children, int depth)
    {
        foreach (var c in children)
        {
            output.WriteLine($"{new string(' ', depth * 2)}{(c.State == "done" ? "[x]" : "[ ]")} {TaskResolver.ShortId(c.Id)}  {c.Title}");
            Children(c.Children, depth + 1);
        }
    }

    private string Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private string When(DateTimeOffset at) => Local(at);
}
