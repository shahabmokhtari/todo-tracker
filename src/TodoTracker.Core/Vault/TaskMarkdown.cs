using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TodoTracker.Core.Vault;

/// <summary>A root task file read from disk, plus everything needed to write it back without losing anything.</summary>
public sealed class ParsedTaskFile
{
    internal ParsedTaskFile(WorkItem root)
    {
        Root = root;
    }

    /// <summary>The task tree (not attached to a board).</summary>
    public WorkItem Root { get; }

    /// <summary>The file lacks metadata the app keeps (ids, exact times, sections). The app adds it the next time it
    /// saves this task for another reason; it never rewrites a file just because it read it.</summary>
    public bool NeedsWrite { get; internal set; }

    /// <summary>The file carries the app's task id (it was created or saved by Todo Tracker).</summary>
    public bool HasAppId { get; internal set; }

    public List<string> Warnings { get; } = [];

    internal Frontmatter Frontmatter { get; set; } = Frontmatter.Empty;

    /// <summary>Frontmatter key holding the app's id: <c>id</c>, or <c>tt-id</c> when the user's own <c>id</c> isn't a GUID.</summary>
    internal string IdKey { get; set; } = "id";

    /// <summary>What each owned property meant when read; unchanged values are written back exactly as typed.</summary>
    internal Dictionary<string, string> KeySignatures { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal Dictionary<Guid, string> BlockIds { get; } = [];

    internal Dictionary<Guid, char> CheckChars { get; } = [];

    internal Dictionary<Guid, string> PriorityEmoji { get; } = [];

    internal Dictionary<Guid, (string Raw, string Path, string Name)> RawLinks { get; } = [];

    /// <summary>Headings or text right above a top-level subtask (e.g. <c>### Phase 1</c>).</summary>
    internal Dictionary<Guid, string> Leading { get; } = [];

    internal string? SubtasksTrailer { get; set; }

    internal string? NotesPreamble { get; set; }

    internal string? AttachmentsPreamble { get; set; }

    /// <summary>Sections after the first app section, in file order: an app section kind, or raw text of another section.</summary>
    internal List<(string? Kind, string? Raw)> Layout { get; } = [];

    internal string Newline { get; set; } = "\n";

    internal bool Bom { get; set; }
}

/// <summary>
/// Reads and writes one root task as Obsidian-friendly markdown (see <c>docs/vault-format.md</c>):
/// YAML frontmatter for the task, <c># Title</c>, free-form details, and <c>## Subtasks</c>/<c>## Steps</c>,
/// <c>## Notes</c>, <c>## Attachments</c> sections. Subtask lines use Obsidian Tasks tokens for what people edit
/// (checkbox, priority, 📅 due, ⏳ scheduled, ✅ done, #tags) and a hidden <c>%%{json}%%</c> comment for exact times.
/// Anything the app doesn't own (other properties, headings, sections, link styles, custom checkbox states) is
/// written back as it was.
/// </summary>
public static partial class TaskMarkdown
{
    private const string Subtasks = "subtasks";
    private const string Notes = "notes";
    private const string Attachments = "attachments";

    private static readonly string[] CanonicalKeys = ["status", "priority", "created", "completed", "due", "scheduled", "sequential", "step-delay", "tags", "labels", "reminders"];

    public static ParsedTaskFile Parse(string text, string fileStem, TaskMarkdownContext context)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            return ParseCore(text, fileStem, context);
        }
        catch (FormatException ex)
        {
            throw new VaultFormatException(ex.Message, ex);
        }
    }

    public static string Render(WorkItem root, TaskMarkdownContext context, ParsedTaskFile? previous = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(context);
        var sb = new StringBuilder();
        sb.Append("---\n");
        RenderFrontmatter(sb, root, context.TimeZone, previous);
        sb.Append("---\n# ").Append(root.Title).Append('\n');

        var blocks = new List<string>();
        if (!string.IsNullOrWhiteSpace(root.Details))
        {
            blocks.Add(VaultText.CloseFences(root.Details.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n')));
        }

        var rendered = new HashSet<string>();
        foreach (var (kind, raw) in previous?.Layout ?? [])
        {
            if (kind is null)
            {
                blocks.Add(raw!);
            }
            else if (rendered.Add(kind) && RenderSection(kind, root, context, previous) is { } section)
            {
                blocks.Add(section);
            }
        }

        foreach (var kind in new[] { Subtasks, Notes, Attachments })
        {
            if (rendered.Add(kind) && RenderSection(kind, root, context, previous) is { } section)
            {
                blocks.Add(section);
            }
        }

        if (blocks.Count > 0)
        {
            sb.Append('\n').Append(string.Join("\n\n", blocks)).Append('\n');
        }

        var result = sb.ToString();
        if (previous is { Newline: "\r\n" })
        {
            result = result.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        return previous is { Bom: true } ? "\uFEFF" + result : result;
    }

    // ---- Parsing ------------------------------------------------------------------------

    private static ParsedTaskFile ParseCore(string text, string fileStem, TaskMarkdownContext ctx)
    {
        var bom = text.StartsWith('\uFEFF');
        if (bom)
        {
            text = text[1..];
        }

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var (fm, body, present) = Frontmatter.Split(text);
        var tz = ctx.TimeZone;
        var state = new ParseState(ctx) { NeedsWrite = !present };

        // The app's id: tt-id, else a GUID id. A non-GUID id belongs to the user (e.g. a Zettelkasten id) and stays.
        var idKey = "id";
        Guid? appId = null;
        if (Guid.TryParse(fm.Scalar("tt-id"), out var ttId))
        {
            (idKey, appId) = ("tt-id", ttId);
        }
        else if (Guid.TryParse(fm.Scalar("id"), out var plainId))
        {
            appId = plainId;
        }
        else if (fm.Has("id"))
        {
            idKey = "tt-id";
        }

        var id = state.NewId(appId, "root");

        // Sections: text before the first app section is the details; later unknown sections keep their place.
        string? title = null;
        var details = new List<string>();
        var sections = new Dictionary<string, List<string>> { [Subtasks] = [], [Notes] = [], [Attachments] = [] };
        var layout = new List<(string? Kind, List<string>? Lines)>();
        var current = details;
        var fence = new VaultText.Fence();
        foreach (var line in body.Split('\n'))
        {
            if (!fence.Accept(line))
            {
                if (title is null && line.StartsWith("# ", StringComparison.Ordinal) && layout.Count == 0)
                {
                    title = line[2..].Trim();
                    continue;
                }

                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    var kind = SectionKind(line[3..]);
                    if (kind is not null)
                    {
                        current = sections[kind];
                        if (!layout.Exists(l => l.Kind == kind))
                        {
                            layout.Add((kind, null));
                        }

                        continue;
                    }

                    if (layout.Count > 0)
                    {
                        current = [];
                        layout.Add((null, current));
                    }
                }
            }

            current.Add(line);
        }

        var hasAppId = appId is not null;
        if (!layout.Exists(l => l.Kind == Subtasks) && !hasAppId && ExtractChecklist(details) is { Count: > 0 } checklist)
        {
            // A note typed in Obsidian: its first checkbox list is the subtasks.
            sections[Subtasks] = checklist;
            state.NeedsWrite = true;
        }

        state.NeedsWrite |= title is null || !hasAppId;
        var created = VaultText.ParseTime(fm.Scalar("created"), tz, "00:00");
        state.NeedsWrite |= created is null;
        var root = new WorkItem(id, string.IsNullOrWhiteSpace(title) ? fileStem : title, ParsePriority(fm.Scalar("priority")), created ?? ctx.Now)
        {
            Details = JoinKeep(details),
            Deadline = VaultText.ParseTime(fm.Scalar("due"), tz, VaultText.DefaultDueTime),
            NextActionAt = VaultText.ParseTime(fm.Scalar("scheduled"), tz, VaultText.DefaultScheduledTime),
            StepDelay = VaultText.ParseDuration(fm.Scalar("step-delay")),
        };
        var result = new ParsedTaskFile(root) { Frontmatter = fm, Newline = newline, Bom = bom, IdKey = idKey, HasAppId = hasAppId };
        state.File = result;

        var status = (fm.Scalar("status") ?? string.Empty).Trim().ToLowerInvariant();
        var done = status is "done" or "completed" or "complete" or "cancelled" or "canceled";
        state.NeedsWrite |= status.Length == 0;
        var completed = VaultText.ParseTime(fm.Scalar("completed"), tz, VaultText.DefaultDoneTime);
        if (done)
        {
            root.CompletedAt = completed ?? ctx.Now;
            state.NeedsWrite |= completed is null;
        }
        else
        {
            state.NeedsWrite |= completed is not null;
        }

        root.TagList.AddRange(fm.List("tags").Select(t => t.TrimStart('#')).Where(t => t.Length > 0));
        root.LabelList.AddRange(fm.List("labels"));
        var reminderIndex = 0;
        foreach (var map in fm.Maps("reminders"))
        {
            if (ParseReminder(map, tz, state, reminderIndex++) is { } reminder)
            {
                root.ReminderList.Add(reminder);
            }
        }

        var ordered = ParseList(sections[Subtasks], root, state);
        root.Sequential = ordered ?? IsTrue(fm.Scalar("sequential"));
        ParseNotes(sections[Notes], root, state);
        ParseAttachments(sections[Attachments], root, state);

        foreach (var (kind, lines) in layout)
        {
            if (kind is not null)
            {
                result.Layout.Add((kind, null));
            }
            else if (JoinKeep(lines!) is { } raw)
            {
                result.Layout.Add((null, raw));
            }
        }

        var signatures = Signatures(root);
        foreach (var key in CanonicalKeys)
        {
            if (fm.Has(key))
            {
                result.KeySignatures[key] = signatures[key];
            }
        }

        result.NeedsWrite = state.NeedsWrite;
        return result;
    }

    private static string? SectionKind(string heading) => heading.Trim().ToLowerInvariant() switch
    {
        "subtasks" or "steps" or "tasks" or "checklist" => Subtasks,
        "notes" or "log" => Notes,
        "attachments" or "files" => Attachments,
        _ => null,
    };

    private sealed class ParseState(TaskMarkdownContext context)
    {
        private readonly HashSet<Guid> _used = [];

        public ParsedTaskFile File { get; set; } = null!;

        public TaskMarkdownContext Context { get; } = context;

        public bool NeedsWrite { get; set; }

        public Dictionary<string, WorkItem> ByBlockId { get; } = new(StringComparer.Ordinal);

        /// <summary>The id from the file if it's new here; else a fresh one (stable per file when the store gave a seed).</summary>
        public Guid NewId(Guid? candidate, string seedKey)
        {
            if (candidate is { } c && c != Guid.Empty && _used.Add(c))
            {
                return c;
            }

            NeedsWrite = true;
            for (var n = 0; ; n++)
            {
                var fresh = Context.IdSeed is { } seed ? VaultText.StableGuid($"{seed}|{seedKey}|{n}") : Guid.NewGuid();
                if (_used.Add(fresh))
                {
                    return fresh;
                }
            }
        }
    }

    private sealed class Frame(WorkItem item, int indent, string seedKey)
    {
        public WorkItem Item { get; } = item;

        public int Indent { get; } = indent;

        public string SeedKey { get; } = seedKey;

        public bool? Ordered { get; set; }

        public List<string> Details { get; } = [];
    }

    /// <summary>The first top-level checkbox list in the details (for files typed without a "## Subtasks" heading).</summary>
    private static List<string>? ExtractChecklist(List<string> details)
    {
        var fence = new VaultText.Fence();
        for (var i = 0; i < details.Count; i++)
        {
            if (fence.Accept(details[i]))
            {
                continue;
            }

            var m = ListItem().Match(details[i]);
            if (!m.Success || m.Groups["ind"].Length > 0 || !m.Groups["c"].Success)
            {
                continue;
            }

            var end = i + 1;
            var inner = new VaultText.Fence();
            while (end < details.Count)
            {
                var line = details[end];
                if (inner.Accept(line))
                {
                    end++;
                    continue;
                }

                if (line.Trim().Length == 0)
                {
                    var next = details.Skip(end + 1).FirstOrDefault(l => l.Trim().Length > 0);
                    if (next is null || !(char.IsWhiteSpace(next[0]) || ListItem().IsMatch(next)))
                    {
                        break;
                    }
                }
                else if (!char.IsWhiteSpace(line[0]) && !ListItem().IsMatch(line))
                {
                    break;
                }

                end++;
            }

            var list = details.GetRange(i, end - i);
            details.RemoveRange(i, end - i);
            return list;
        }

        return null;
    }

    /// <summary>Parses the subtasks list into <paramref name="root"/>; returns whether its top-level list is ordered.</summary>
    private static bool? ParseList(List<string> lines, WorkItem root, ParseState state)
    {
        var frames = new List<Frame> { new(root, -1, string.Empty) };
        var created = new List<Frame>();
        var leading = new List<string>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var pendingBlank = 0;
        var fence = new VaultText.Fence();
        List<string>? fenceTarget = null;
        var fenceStrip = 0;
        foreach (var line in lines)
        {
            if (fence.IsOpen)
            {
                // Inside a code block: every line belongs to whoever opened it (examples never become tasks).
                fenceTarget!.Add(fenceStrip == 0 ? line : StripColumns(line, fenceStrip));
                fence.Accept(line);
                continue;
            }

            if (line.Trim().Length == 0)
            {
                pendingBlank++;
                continue;
            }

            var indent = Columns(line);
            var m = ListItem().Match(line);
            if (m.Success)
            {
                while (frames.Count > 1 && frames[^1].Indent >= indent)
                {
                    frames.RemoveAt(frames.Count - 1);
                }

                var parent = frames[^1];
                var rest = m.Groups["rest"].Value;
                if (m.Groups["c"].Success || frames.Count == 1)
                {
                    var titleKey = $"{parent.SeedKey}/{TitleForSeed(rest)}";
                    occurrences[titleKey] = occurrences.GetValueOrDefault(titleKey) + 1;
                    var item = ParseItem(m.Groups["c"].Success ? m.Groups["c"].Value[0] : ' ', rest, state, isPlain: !m.Groups["c"].Success, $"{titleKey}#{occurrences[titleKey]}");
                    item.Parent = parent.Item;
                    parent.Item.ChildList.Add(item);
                    parent.Ordered ??= char.IsDigit(m.Groups["m"].Value[0]);
                    if (frames.Count == 1 && JoinKeep(leading) is { } heading)
                    {
                        state.File.Leading[item.Id] = heading;
                    }

                    leading.Clear();
                    var frame = new Frame(item, indent, titleKey);
                    frames.Add(frame);
                    created.Add(frame);
                    pendingBlank = 0;
                    continue;
                }

                if (rest.StartsWith("📎", StringComparison.Ordinal) && ParseAttachmentLine(rest[2..].Trim(), state) is { } attachment)
                {
                    parent.Item.AttachmentList.Add(attachment);
                    pendingBlank = 0;
                    continue;
                }
            }
            else
            {
                while (frames.Count > 1 && indent <= frames[^1].Indent)
                {
                    frames.RemoveAt(frames.Count - 1);
                }
            }

            var topLevel = frames.Count == 1;
            var bucket = topLevel ? leading : frames[^1].Details;
            if (bucket.Count > 0)
            {
                bucket.AddRange(Enumerable.Repeat(string.Empty, pendingBlank));
            }

            pendingBlank = 0;
            var strip = topLevel ? 0 : frames[^1].Indent + 4;
            var text = topLevel ? line : StripColumns(line, strip);
            bucket.Add(text);
            if (fence.Accept(text))
            {
                fenceTarget = bucket;
                fenceStrip = strip;
            }
        }

        foreach (var frame in created)
        {
            frame.Item.Details = JoinKeep(frame.Details);
            frame.Item.Sequential = frame.Ordered ?? frame.Item.Sequential;
        }

        state.File.SubtasksTrailer = JoinKeep(leading);
        return frames[0].Ordered;
    }

    /// <summary>
    /// The part of a line that identifies a hand-written subtask: its words, without what people (or the Obsidian Tasks
    /// plugin) add as they work on it — the checkbox, dates such as ✅ 2026-01-05, priority, tags, block ids, case.
    /// </summary>
    private static string TitleForSeed(string rest)
    {
        var text = HiddenMeta().Replace(rest, string.Empty);
        text = TrailingBlockId().Replace(text, string.Empty);
        text = SeedNoise().Replace(text, " ");
        return Spaces().Replace(text, " ").Trim().ToLowerInvariant();
    }

    [GeneratedRegex(@"(?:📅|⏳|✅|➕|🛫|❌)\uFE0F?\s*\d{4}-\d{2}-\d{2}|🔺|⏫|🔼|🔽|⏬|🔁|(?<=^|\s)#[\p{L}\p{N}_\-/]+|\uFE0F")]
    private static partial Regex SeedNoise();

    private static WorkItem ParseItem(char check, string rest, ParseState state, bool isPlain, string seedKey)
    {
        var ctx = state.Context;
        var tz = ctx.TimeZone;
        JsonObject? meta = null;
        var hidden = HiddenMeta().Match(rest);
        if (hidden.Success)
        {
            meta = TryParseMeta(hidden.Groups["json"].Value);
            if (meta is null)
            {
                state.File.Warnings.Add($"Ignored unreadable metadata on \"{rest}\".");
                state.NeedsWrite = true;
            }

            rest = rest[..hidden.Index];
        }

        string? blockId = null;
        var block = TrailingBlockId().Match(rest);
        if (block.Success)
        {
            blockId = block.Groups["bid"].Value;
            rest = rest[..block.Index];
        }

        DateOnly? due = null, scheduled = null, doneDate = null;
        rest = DateToken().Replace(rest, t =>
        {
            var date = VaultText.ParseDate(t.Groups["d"].Value);
            switch (t.Groups["e"].Value)
            {
                case "📅":
                    due = date;
                    break;
                case "⏳":
                    scheduled = date;
                    break;
                default:
                    doneDate = date;
                    break;
            }

            return " ";
        });

        var priority = Priority.Normal;
        string? emoji = null;
        rest = PriorityToken().Replace(rest, t =>
        {
            emoji = t.Groups[1].Value;
            priority = PriorityOf(emoji);
            return " ";
        });

        var title = Spaces().Replace(rest, " ").Trim();
        var trailing = new List<string>();
        while (TrailingTag().Match(title) is { Success: true } tag && IsTag(tag.Groups["t"].Value))
        {
            trailing.Insert(0, tag.Groups["t"].Value);
            title = title[..tag.Index].TrimEnd();
        }

        var tags = InlineTag().Matches(title).Select(t => t.Groups["t"].Value).Where(IsTag).Concat(trailing)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (title.Length == 0)
        {
            title = trailing.Count > 0 ? "#" + trailing[0] : "(untitled)";
        }

        var id = state.NewId(Guid.TryParse(Str(meta, "id"), out var g) ? g : null, seedKey);
        var createdMeta = VaultText.ParseInstant(Str(meta, "created"));
        state.NeedsWrite |= createdMeta is null || isPlain;
        var item = new WorkItem(id, title, priority, createdMeta ?? ctx.Now)
        {
            Deadline = Resolve(due, Str(meta, "due"), VaultText.DefaultDueTime),
            NextActionAt = Resolve(scheduled, Str(meta, "next"), VaultText.DefaultScheduledTime),
            Sequential = meta?["seq"] is JsonValue seq && seq.TryGetValue<bool>(out var s) && s,
            StepDelay = Long(meta, "delay") is { } delay ? TimeSpan.FromMinutes(delay) : null,
        };
        item.TagList.AddRange(tags);
        if (meta?["labels"] is JsonArray labels)
        {
            item.LabelList.AddRange(labels.OfType<JsonValue>().Select(l => l.TryGetValue<string>(out var v) ? v : null).OfType<string>());
        }

        var reminderIndex = 0;
        foreach (var r in meta?["reminders"] as JsonArray ?? [])
        {
            if (r is JsonObject o && VaultText.ParseInstant(Str(o, "at")) is { } at)
            {
                var reminderId = state.NewId(Guid.TryParse(Str(o, "id"), out var rid) ? rid : null, $"{seedKey}|r{reminderIndex++}");
                item.ReminderList.Add(new Reminder(reminderId, at, Str(o, "msg") ?? string.Empty, Str(o, "kind") == "next-action" ? ReminderKind.NextAction : ReminderKind.Manual)
                {
                    NotifiedAt = VaultText.ParseInstant(Str(o, "notified")),
                    DismissedAt = VaultText.ParseInstant(Str(o, "dismissed")),
                });
            }
        }

        if (check is not (' ' or 'x'))
        {
            state.File.CheckChars[id] = check;
        }

        if (emoji is "🔼" or "⏬")
        {
            state.File.PriorityEmoji[id] = emoji;
        }

        var doneMeta = VaultText.ParseInstant(Str(meta, "done"));
        if (IsDoneChar(check))
        {
            item.CompletedAt = doneDate is { } d && (doneMeta is null || DateOnly.FromDateTime(VaultText.ToLocal(doneMeta.Value, tz).DateTime) != d)
                ? VaultText.AtLocal(d, VaultText.DefaultDoneTime, tz)
                : doneMeta ?? ctx.Now;
            state.NeedsWrite |= doneMeta is null;
        }
        else
        {
            state.NeedsWrite |= doneMeta is not null || doneDate is not null;
        }

        if (blockId is not null)
        {
            if (state.ByBlockId.TryAdd(blockId, item) && blockId != VaultText.BlockId(id))
            {
                state.File.BlockIds[id] = blockId;
            }
        }
        else
        {
            state.NeedsWrite = true;
        }

        state.ByBlockId.TryAdd(VaultText.BlockId(id), item);
        return item;

        DateTimeOffset? Resolve(DateOnly? visible, string? exact, string defaultTime)
        {
            var precise = VaultText.ParseInstant(exact);
            if (visible is null)
            {
                state.NeedsWrite |= precise is not null;
                return null;
            }

            if (precise is { } p && DateOnly.FromDateTime(VaultText.ToLocal(p, tz).DateTime) == visible)
            {
                return p;
            }

            state.NeedsWrite = true;
            return VaultText.AtLocal(visible.Value, defaultTime, tz);
        }
    }

    private static void ParseNotes(List<string> lines, WorkItem root, ParseState state)
    {
        var tz = state.Context.TimeZone;
        var preamble = new List<string>();
        var i = 0;
        var pendingBlank = 0;
        var index = 0;
        while (i < lines.Count)
        {
            var header = CalloutHeader().Match(lines[i]);
            if (!header.Success)
            {
                if (lines[i].Trim().Length == 0)
                {
                    pendingBlank++;
                }
                else
                {
                    if (preamble.Count > 0)
                    {
                        preamble.AddRange(Enumerable.Repeat(string.Empty, pendingBlank));
                    }

                    pendingBlank = 0;
                    preamble.Add(lines[i]);
                }

                i++;
                continue;
            }

            var head = header.Groups["head"].Value;
            var seedKey = $"note|{index++}|{head}";
            JsonObject? meta = null;
            if (HiddenMeta().Match(head) is { Success: true } hidden)
            {
                meta = TryParseMeta(hidden.Groups["json"].Value);
                head = head[..hidden.Index];
            }

            var body = new List<string>();
            for (i++; i < lines.Count && lines[i].StartsWith('>'); i++)
            {
                body.Add(lines[i].Length > 1 && lines[i][1] == ' ' ? lines[i][2..] : lines[i][1..]);
            }

            var parts = head.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            DateTimeOffset? visible = null;
            if (parts.Count > 0 && NoteTime().IsMatch(parts[0]))
            {
                visible = VaultText.ParseTime(parts[0], tz, "00:00");
                parts.RemoveAt(0);
            }

            var target = root;
            var link = parts.FindIndex(p => BlockLink().IsMatch(p));
            if (link >= 0)
            {
                var bid = BlockLink().Match(parts[link]).Groups["bid"].Value;
                target = state.ByBlockId.GetValueOrDefault(bid) ?? root;
                parts.RemoveAt(link);
            }

            var author = VaultText.ParseActor(parts.Count > 0 ? parts[0] : null);
            if (visible is null && parts.Count > 0 && author.Kind == ActorKind.User && author.Name is not null)
            {
                // "> [!note] Meeting with Ana" is a heading, not an author.
                body.Insert(0, string.Join(" · ", parts));
                author = Actor.User;
            }

            string? sourceUrl = null, sourceTitle = null;
            if (body.Count > 0 && SourceLine().Match(body[^1]) is { Success: true } source)
            {
                sourceUrl = source.Groups["u"].Value;
                sourceTitle = source.Groups["t"].Success && source.Groups["t"].Value.Length > 0 ? source.Groups["t"].Value : null;
                body.RemoveAt(body.Count - 1);
            }

            var text = JoinKeep(body);
            var exact = VaultText.ParseInstant(Str(meta, "at"));
            var at = exact is { } e && (visible is null || VaultText.LocalMinute(e, tz) == VaultText.LocalMinute(visible.Value, tz)) ? e : visible ?? state.Context.Now;
            state.NeedsWrite |= meta is null || exact != at;
            if (text is null)
            {
                state.NeedsWrite = true;
                continue;
            }

            var id = state.NewId(Guid.TryParse(Str(meta, "id"), out var g) ? g : null, seedKey);
            target.NoteList.Add(new Note(id, at, text, author, sourceUrl, sourceTitle));
        }

        state.File.NotesPreamble = JoinKeep(preamble);
    }

    private static void ParseAttachments(List<string> lines, WorkItem root, ParseState state)
    {
        var preamble = new List<string>();
        foreach (var line in lines)
        {
            var m = ListItem().Match(line);
            if (m.Success && !m.Groups["c"].Success && m.Groups["ind"].Length == 0 && ParseAttachmentLine(m.Groups["rest"].Value, state) is { } attachment)
            {
                root.AttachmentList.Add(attachment);
            }
            else if (line.Trim().Length > 0 || preamble.Count > 0)
            {
                preamble.Add(line);
            }
        }

        state.File.AttachmentsPreamble = JoinKeep(preamble);
    }

    private static Attachment? ParseAttachmentLine(string rest, ParseState state)
    {
        JsonObject? meta = null;
        if (HiddenMeta().Match(rest) is { Success: true } hidden)
        {
            meta = TryParseMeta(hidden.Groups["json"].Value);
            rest = rest[..hidden.Index];
        }

        rest = rest.Trim();
        string name, path;
        if (MarkdownLink().Match(rest) is { Success: true } md && md.Length == rest.Length)
        {
            var url = md.Groups["url"].Value.Trim('<', '>');
            if (url.Contains("://", StringComparison.Ordinal))
            {
                return null;
            }

            path = ResolveRelative(state.Context.FileDirectory, Uri.UnescapeDataString(url));
            name = md.Groups["name"].Value.Length > 0 ? md.Groups["name"].Value : path[(path.LastIndexOf('/') + 1)..];
        }
        else if (WikiLink().Match(rest) is { Success: true } w && w.Length == rest.Length)
        {
            path = w.Groups["target"].Value.Trim();
            name = path[(path.LastIndexOf('/') + 1)..];
        }
        else
        {
            return null;
        }

        var id = state.NewId(Guid.TryParse(Str(meta, "id"), out var g) ? g : null, $"attachment|{path}");
        var at = VaultText.ParseInstant(Str(meta, "at"));
        state.NeedsWrite |= at is null;
        var attachment = new Attachment(id, name, path, Long(meta, "size") ?? 0, at ?? state.Context.Now, VaultText.ParseActor(Str(meta, "by")));
        state.File.RawLinks[id] = (rest, path, name);
        return attachment;
    }

    private static Reminder? ParseReminder(YamlDotNet.RepresentationModel.YamlMappingNode map, TimeZoneInfo tz, ParseState state, int index)
    {
        var at = VaultText.ParseTime(Frontmatter.Get(map, "at"), tz, "09:00");
        if (at is null)
        {
            return null;
        }

        var kind = (Frontmatter.Get(map, "kind") ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Equals("nextaction", StringComparison.OrdinalIgnoreCase)
            ? ReminderKind.NextAction
            : ReminderKind.Manual;
        var id = state.NewId(Guid.TryParse(Frontmatter.Get(map, "id"), out var g) ? g : null, $"reminder|{index}");
        return new Reminder(id, at.Value, Frontmatter.Get(map, "message") ?? string.Empty, kind)
        {
            NotifiedAt = VaultText.ParseTime(Frontmatter.Get(map, "notified"), tz, "00:00"),
            DismissedAt = VaultText.ParseTime(Frontmatter.Get(map, "dismissed"), tz, "00:00"),
        };
    }

    // ---- Rendering ----------------------------------------------------------------------

    /// <summary>What each owned property means, to tell whether the value changed since it was read.</summary>
    private static Dictionary<string, string> Signatures(WorkItem root) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["status"] = root.IsDone ? "done" : "open",
        ["priority"] = root.Priority.ToString(),
        ["created"] = VaultText.Utc(root.CreatedAt),
        ["completed"] = root.CompletedAt is { } c ? VaultText.Utc(c) : string.Empty,
        ["due"] = root.Deadline is { } d ? VaultText.Utc(d) : string.Empty,
        ["scheduled"] = root.NextActionAt is { } n ? VaultText.Utc(n) : string.Empty,
        ["sequential"] = root.Sequential ? "true" : "false",
        ["step-delay"] = root.StepDelay is { } s ? ((long)s.TotalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty,
        ["tags"] = string.Join('\u001f', root.Tags),
        ["labels"] = string.Join('\u001f', root.Labels),
        ["reminders"] = string.Join('\u001e', root.Reminders.Select(r => $"{r.Id}|{VaultText.Utc(r.DueAt)}|{r.Message}|{r.Kind}|{r.NotifiedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}|{r.DismissedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}")),
    };

    private static void RenderFrontmatter(StringBuilder sb, WorkItem root, TimeZoneInfo tz, ParsedTaskFile? previous)
    {
        var idKey = previous?.IdKey ?? "id";
        var signatures = Signatures(root);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, raw) in previous?.Frontmatter.Blocks ?? [])
        {
            if (key is null)
            {
                sb.Append(raw);
                continue;
            }

            var owned = string.Equals(key, idKey, StringComparison.OrdinalIgnoreCase) || CanonicalKeys.Contains(key, StringComparer.OrdinalIgnoreCase);
            if (!owned)
            {
                sb.Append(raw);
            }
            else if (emitted.Add(key))
            {
                // An unchanged value is written exactly as the user typed it (date-only due, flow lists, quotes…).
                var unchanged = previous!.KeySignatures.TryGetValue(key, out var before) && before == signatures.GetValueOrDefault(key);
                sb.Append(unchanged ? raw : CanonicalProperty(key, idKey, root, tz) ?? string.Empty);
            }
        }

        foreach (var key in CanonicalKeys.Prepend(idKey))
        {
            if (emitted.Add(key))
            {
                sb.Append(CanonicalProperty(key, idKey, root, tz) ?? string.Empty);
            }
        }
    }

    private static string? CanonicalProperty(string key, string idKey, WorkItem root, TimeZoneInfo tz)
    {
        if (string.Equals(key, idKey, StringComparison.OrdinalIgnoreCase))
        {
            return $"{idKey}: {root.Id}\n";
        }

        switch (key.ToLowerInvariant())
        {
            case "status":
                return $"status: {(root.IsDone ? "done" : "open")}\n";
            case "priority":
                return root.Priority == Priority.Normal ? null : $"priority: {root.Priority.ToString().ToLowerInvariant()}\n";
            case "created":
                return $"created: {VaultText.Utc(root.CreatedAt)}\n";
            case "completed":
                return root.CompletedAt is { } c ? $"completed: {VaultText.Utc(c)}\n" : null;
            case "due":
                return root.Deadline is { } d ? $"due: {VaultText.LocalDateTime(d, tz)}\n" : null;
            case "scheduled":
                return root.NextActionAt is { } n ? $"scheduled: {VaultText.LocalDateTime(n, tz)}\n" : null;
            case "sequential":
                return root.Sequential ? "sequential: true\n" : null;
            case "step-delay":
                return root.StepDelay is { } s ? $"step-delay: {VaultText.Duration(s)}\n" : null;
            case "tags":
                return List("tags", root.Tags);
            case "labels":
                return List("labels", root.Labels);
            case "reminders":
                return Reminders(root.Reminders);
            default:
                return null;
        }

        static string? List(string key, IReadOnlyList<string> values) =>
            values.Count == 0 ? null : $"{key}:\n" + string.Concat(values.Select(v => $"  - {VaultText.YamlScalar(v)}\n"));

        static string? Reminders(IReadOnlyList<Reminder> reminders)
        {
            if (reminders.Count == 0)
            {
                return null;
            }

            var sb = new StringBuilder("reminders:\n");
            foreach (var r in reminders)
            {
                sb.Append("  - id: ").Append(r.Id).Append('\n');
                sb.Append("    at: ").Append(VaultText.Utc(r.DueAt)).Append('\n');
                sb.Append("    message: ").Append(JsonSerializer.Serialize(r.Message, VaultText.HiddenJson)).Append('\n');
                sb.Append("    kind: ").Append(r.Kind == ReminderKind.NextAction ? "next-action" : "manual").Append('\n');
                if (r.NotifiedAt is { } n)
                {
                    sb.Append("    notified: ").Append(VaultText.Utc(n)).Append('\n');
                }

                if (r.DismissedAt is { } d)
                {
                    sb.Append("    dismissed: ").Append(VaultText.Utc(d)).Append('\n');
                }
            }

            return sb.ToString();
        }
    }

    private static string? RenderSection(string kind, WorkItem root, TaskMarkdownContext context, ParsedTaskFile? previous)
    {
        switch (kind)
        {
            case Subtasks:
            {
                var list = new StringBuilder();
                var current = root.Children.Select(c => c.Id).ToHashSet();
                for (var i = 0; i < root.Children.Count; i++)
                {
                    var item = root.Children[i];
                    if (previous?.Leading.GetValueOrDefault(item.Id) is { } heading)
                    {
                        if (list.Length > 0)
                        {
                            list.Append('\n');
                        }

                        list.Append(VaultText.CloseFences(heading)).Append("\n\n");
                    }

                    RenderItem(list, item, i, root.Sequential, 0, context, previous);
                }

                // Text that sat above subtasks that are gone now, and text after the list, stays at the end.
                var rest = (previous?.Leading.Where(l => !current.Contains(l.Key)).Select(l => l.Value) ?? [])
                    .Concat(previous?.SubtasksTrailer is { } t ? [t] : [])
                    .ToList();
                if (rest.Count > 0)
                {
                    list.Append(list.Length > 0 ? "\n" : string.Empty).Append(VaultText.CloseFences(string.Join("\n\n", rest))).Append('\n');
                }

                return list.Length == 0 ? null : $"## {(root.Sequential ? "Steps" : "Subtasks")}\n\n{list.ToString().TrimEnd('\n')}";
            }

            case Notes:
            {
                var notes = root.SelfAndDescendants().SelectMany(i => i.Notes.Select(n => (Item: i, Note: n))).OrderBy(n => n.Note.At).ToList();
                if (notes.Count == 0 && previous?.NotesPreamble is null)
                {
                    return null;
                }

                return Section("Notes", previous?.NotesPreamble, string.Join("\n\n", notes.Select(n => RenderNote(n.Item, n.Note, root, context.TimeZone, previous))));
            }

            default:
            {
                if (root.Attachments.Count == 0 && previous?.AttachmentsPreamble is null)
                {
                    return null;
                }

                return Section("Attachments", previous?.AttachmentsPreamble, string.Join("\n", root.Attachments.Select(a => "- " + RenderAttachment(a, context, previous))));
            }
        }
    }

    private static void RenderItem(StringBuilder sb, WorkItem item, int index, bool ordered, int depth, TaskMarkdownContext ctx, ParsedTaskFile? previous)
    {
        var indent = new string('\t', depth);
        sb.Append(indent).Append(ordered ? $"{index + 1}." : "-").Append(" [").Append(CheckChar(item, previous)).Append("] ")
            .Append(ItemLine(item, ctx.TimeZone, previous)).Append('\n');
        if (!string.IsNullOrEmpty(item.Details))
        {
            foreach (var line in VaultText.CloseFences(item.Details.Replace("\r\n", "\n", StringComparison.Ordinal)).Split('\n'))
            {
                sb.Append(line.Length == 0 ? string.Empty : indent + "\t" + line).Append('\n');
            }
        }

        foreach (var attachment in item.Attachments)
        {
            sb.Append(indent).Append("\t- 📎 ").Append(RenderAttachment(attachment, ctx, previous)).Append('\n');
        }

        for (var i = 0; i < item.Children.Count; i++)
        {
            RenderItem(sb, item.Children[i], i, item.Sequential, depth + 1, ctx, previous);
        }
    }

    private static char CheckChar(WorkItem item, ParsedTaskFile? previous) =>
        previous?.CheckChars.TryGetValue(item.Id, out var c) == true && IsDoneChar(c) == item.IsDone ? c : item.IsDone ? 'x' : ' ';

    private static string ItemLine(WorkItem item, TimeZoneInfo tz, ParsedTaskFile? previous)
    {
        var sb = new StringBuilder(item.Title);
        foreach (var tag in item.Tags)
        {
            if (!Regex.IsMatch(item.Title, @"(^|\s)#" + Regex.Escape(tag) + @"(\s|$)", RegexOptions.IgnoreCase))
            {
                sb.Append(" #").Append(tag);
            }
        }

        var emoji = previous?.PriorityEmoji.GetValueOrDefault(item.Id) is { } kept && PriorityOf(kept) == item.Priority
            ? kept
            : item.Priority switch
            {
                Priority.Critical => "🔺",
                Priority.High => "⏫",
                Priority.Low => "🔽",
                _ => null,
            };
        if (emoji is not null)
        {
            sb.Append(' ').Append(emoji);
        }

        if (item.Deadline is { } due)
        {
            sb.Append(" 📅 ").Append(VaultText.LocalDate(due, tz));
        }

        if (item.NextActionAt is { } next)
        {
            sb.Append(" ⏳ ").Append(VaultText.LocalDate(next, tz));
        }

        if (item.CompletedAt is { } done)
        {
            sb.Append(" ✅ ").Append(VaultText.LocalDate(done, tz));
        }

        sb.Append(" ^").Append(BlockIdOf(item, previous));
        var meta = new JsonObject
        {
            ["id"] = item.Id.ToString(),
            ["created"] = VaultText.Utc(item.CreatedAt),
        };
        if (item.CompletedAt is { } c)
        {
            meta["done"] = VaultText.Utc(c);
        }

        if (item.Deadline is { } d)
        {
            meta["due"] = VaultText.Utc(d);
        }

        if (item.NextActionAt is { } n)
        {
            meta["next"] = VaultText.Utc(n);
        }

        if (item.Sequential && item.Children.Count == 0)
        {
            meta["seq"] = true;
        }

        if (item.StepDelay is { } delay)
        {
            meta["delay"] = (long)Math.Round(delay.TotalMinutes);
        }

        if (item.Labels.Count > 0)
        {
            meta["labels"] = new JsonArray(item.Labels.Select(l => (JsonNode?)l).ToArray());
        }

        if (item.Reminders.Count > 0)
        {
            var reminders = new JsonArray();
            foreach (var r in item.Reminders)
            {
                var o = new JsonObject
                {
                    ["id"] = r.Id.ToString(),
                    ["at"] = VaultText.Utc(r.DueAt),
                    ["msg"] = r.Message,
                    ["kind"] = r.Kind == ReminderKind.NextAction ? "next-action" : "manual",
                };
                if (r.NotifiedAt is { } nt)
                {
                    o["notified"] = VaultText.Utc(nt);
                }

                if (r.DismissedAt is { } dm)
                {
                    o["dismissed"] = VaultText.Utc(dm);
                }

                reminders.Add(o);
            }

            meta["reminders"] = reminders;
        }

        sb.Append(" %%").Append(VaultText.Hidden(meta)).Append("%%");
        return sb.ToString();
    }

    private static string RenderNote(WorkItem item, Note note, WorkItem root, TimeZoneInfo tz, ParsedTaskFile? previous)
    {
        var sb = new StringBuilder("> [!note] ");
        sb.Append(VaultText.LocalMinute(note.At, tz)).Append(" · ").Append(note.Author.DisplayName);
        if (item != root)
        {
            sb.Append(" · [[#^").Append(BlockIdOf(item, previous)).Append('|').Append(LinkAlias().Replace(item.Title, " ")).Append("]]");
        }

        sb.Append(" %%").Append(VaultText.Hidden(new JsonObject { ["id"] = note.Id.ToString(), ["at"] = VaultText.Utc(note.At) })).Append("%%");
        foreach (var line in note.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            sb.Append('\n').Append(line.Length == 0 ? ">" : "> " + line);
        }

        if (note.SourceUrl is { } url)
        {
            sb.Append("\n> 🔗 ").Append(note.SourceTitle is { Length: > 0 } t ? $"[{LinkAlias().Replace(t, " ")}]({url})" : $"<{url}>");
        }

        return sb.ToString();
    }

    private static string RenderAttachment(Attachment attachment, TaskMarkdownContext ctx, ParsedTaskFile? previous)
    {
        // The link exactly as written (wiki link, embed, alias/size) as long as it still points to the same file.
        var link = previous?.RawLinks.TryGetValue(attachment.Id, out var raw) == true && raw.Path == attachment.Path && raw.Name == attachment.FileName
            ? raw.Raw
            : $"[{LinkAlias().Replace(attachment.FileName, " ")}]({EscapeUrl(RelativeTo(ctx.FileDirectory, attachment.Path))})";
        var meta = new JsonObject
        {
            ["id"] = attachment.Id.ToString(),
            ["size"] = attachment.Size,
            ["at"] = VaultText.Utc(attachment.AddedAt),
            ["by"] = attachment.AddedBy.DisplayName,
        };
        return link + " %%" + VaultText.Hidden(meta) + "%%";
    }

    private static string Section(string heading, string? preamble, string content)
    {
        var parts = new[] { preamble, content }.Where(p => !string.IsNullOrEmpty(p));
        return $"## {heading}\n\n" + string.Join("\n\n", parts);
    }

    private static string BlockIdOf(WorkItem item, ParsedTaskFile? previous) =>
        previous?.BlockIds.GetValueOrDefault(item.Id) ?? VaultText.BlockId(item.Id);

    // ---- Helpers ------------------------------------------------------------------------

    /// <summary>The lines without leading/trailing blank lines, otherwise exactly as written (hard line breaks stay).</summary>
    private static string? JoinKeep(List<string> lines)
    {
        var start = lines.FindIndex(l => l.Trim().Length > 0);
        if (start < 0)
        {
            return null;
        }

        var end = lines.FindLastIndex(l => l.Trim().Length > 0);
        return string.Join('\n', lines.Skip(start).Take(end - start + 1));
    }

    private static int Columns(string line)
    {
        var col = 0;
        foreach (var c in line)
        {
            if (c == '\t')
            {
                col = ((col / 4) + 1) * 4;
            }
            else if (c == ' ')
            {
                col++;
            }
            else
            {
                break;
            }
        }

        return col;
    }

    private static string StripColumns(string line, int columns)
    {
        var col = 0;
        var i = 0;
        while (i < line.Length && col < columns && line[i] is ' ' or '\t')
        {
            col = line[i] == '\t' ? ((col / 4) + 1) * 4 : col + 1;
            i++;
        }

        return line[i..];
    }

    private static string ResolveRelative(string directory, string relative)
    {
        var parts = directory.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var segment in relative.Replace('\\', '/').Split('/'))
        {
            if (segment == "..")
            {
                // Keep ".." that climbs above the vault so the store can refuse it instead of silently clamping.
                if (parts.Count > 0 && parts[^1] != "..")
                {
                    parts.RemoveAt(parts.Count - 1);
                }
                else
                {
                    parts.Add("..");
                }
            }
            else if (segment is not ("." or ""))
            {
                parts.Add(segment);
            }
        }

        return string.Join('/', parts);
    }

    private static string RelativeTo(string directory, string path)
    {
        var from = directory.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var to = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var common = 0;
        while (common < from.Length && common < to.Length - 1 && string.Equals(from[common], to[common], StringComparison.OrdinalIgnoreCase))
        {
            common++;
        }

        return string.Join('/', Enumerable.Repeat("..", from.Length - common).Concat(to.Skip(common)));
    }

    private static string EscapeUrl(string path) =>
        path.Replace("%", "%25", StringComparison.Ordinal).Replace(" ", "%20", StringComparison.Ordinal)
            .Replace("(", "%28", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal);

    private static Priority ParsePriority(string? text) => (text ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "critical" or "urgent" or "highest" => Priority.Critical,
        "high" => Priority.High,
        "low" or "lowest" => Priority.Low,
        _ => Priority.Normal,
    };

    private static Priority PriorityOf(string emoji) => emoji switch
    {
        "🔺" => Priority.Critical,
        "⏫" => Priority.High,
        "🔽" or "⏬" => Priority.Low,
        _ => Priority.Normal,
    };

    private static bool IsDoneChar(char c) => c is 'x' or 'X' or '-';

    private static bool IsTrue(string? text) => (text ?? string.Empty).Trim().ToLowerInvariant() is "true" or "yes";

    private static bool IsTag(string tag) => tag.Any(c => !char.IsDigit(c) && c != '/') && !tag.EndsWith('/');

    private static string? Str(JsonObject? meta, string key) =>
        meta?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Long(JsonObject? meta, string key) =>
        meta?[key] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;

    private static JsonObject? TryParseMeta(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^(?<ind>[ \t]*)(?<m>[-*+]|\d+[.)])[ \t]+(?:\[(?<c>[^\]])\][ \t]?)?(?<rest>.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"\s*%%(?<json>\{.*\})%%\s*$")]
    private static partial Regex HiddenMeta();

    [GeneratedRegex(@"(^|\s+)\^(?<bid>[A-Za-z0-9-]+)\s*$")]
    private static partial Regex TrailingBlockId();

    [GeneratedRegex(@"(?<e>📅|⏳|✅)\uFE0F?\s*(?<d>\d{4}-\d{2}-\d{2})")]
    private static partial Regex DateToken();

    [GeneratedRegex(@"(🔺|⏫|🔼|🔽|⏬)\uFE0F?")]
    private static partial Regex PriorityToken();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"(^|\s+)#(?<t>[\p{L}\p{N}_\-/]+)$")]
    private static partial Regex TrailingTag();

    [GeneratedRegex(@"(?<=^|\s)#(?<t>[\p{L}\p{N}_\-/]+)")]
    private static partial Regex InlineTag();

    [GeneratedRegex(@"^>\s*\[!(?<type>[^\]]+)\][+-]?\s*(?<head>.*)$")]
    private static partial Regex CalloutHeader();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}")]
    private static partial Regex NoteTime();

    [GeneratedRegex(@"\[\[#\^(?<bid>[^|\]]+)(\|[^\]]*)?\]\]")]
    private static partial Regex BlockLink();

    [GeneratedRegex(@"^🔗\s*(?:\[(?<t>[^\]]*)\]\((?<u>https?://[^)\s]+)\)|<?(?<u>https?://[^>\s]+)>?)\s*$")]
    private static partial Regex SourceLine();

    [GeneratedRegex(@"^!?\[(?<name>[^\]]*)\]\((?<url><[^>]+>|[^)\s]+)\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"^!?\[\[(?<target>[^\]|#]+)(?:\|(?<alias>[^\]]*))?\]\]")]
    private static partial Regex WikiLink();

    [GeneratedRegex(@"[\[\]|]")]
    private static partial Regex LinkAlias();
}
