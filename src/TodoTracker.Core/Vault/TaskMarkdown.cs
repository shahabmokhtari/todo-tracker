using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TodoTracker.Core.Vault;

/// <summary>A root task file read from disk, plus what is needed to write it back without losing anything.</summary>
public sealed class ParsedTaskFile
{
    internal ParsedTaskFile(WorkItem root)
    {
        Root = root;
    }

    /// <summary>The task tree (not attached to a board).</summary>
    public WorkItem Root { get; }

    /// <summary>The file lacked something the app needs (ids, timestamps, canonical sections) and should be rewritten.</summary>
    public bool NeedsWrite { get; internal set; }

    public List<string> Warnings { get; } = [];

    internal Frontmatter Frontmatter { get; set; } = Frontmatter.Empty;

    internal Dictionary<Guid, string> BlockIds { get; } = [];

    internal HashSet<Guid> WikiLinks { get; } = [];

    internal string? SubtasksPreamble { get; set; }

    internal string? NotesPreamble { get; set; }

    internal string? AttachmentsPreamble { get; set; }

    internal string Newline { get; set; } = "\n";

    internal bool Bom { get; set; }
}

/// <summary>
/// Reads and writes one root task as Obsidian-friendly markdown (see <c>docs/vault-format.md</c>):
/// YAML frontmatter for the task, <c># Title</c>, free-form details, and <c>## Subtasks</c>/<c>## Steps</c>,
/// <c>## Notes</c>, <c>## Attachments</c> sections. Subtask lines use Obsidian Tasks tokens for what people edit
/// (checkbox, priority, 📅 due, ⏳ scheduled, ✅ done, #tags) and a hidden <c>%%{json}%%</c> comment for exact times.
/// </summary>
public static partial class TaskMarkdown
{
    private static readonly string[] OwnedKeys = ["id", "status", "priority", "created", "completed", "due", "scheduled", "sequential", "step-delay", "tags", "labels", "reminders"];

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
        var tz = context.TimeZone;
        var sb = new StringBuilder();
        sb.Append("---\n");
        RenderFrontmatter(sb, root, tz, previous);
        sb.Append("---\n# ").Append(root.Title).Append('\n');

        var blocks = new List<string>();
        if (!string.IsNullOrWhiteSpace(root.Details))
        {
            blocks.Add(root.Details.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n'));
        }

        if (root.Children.Count > 0 || previous?.SubtasksPreamble is not null)
        {
            var list = new StringBuilder();
            RenderItems(list, root.Children, root.Sequential, 0, context, previous);
            blocks.Add(Section(root.Sequential ? "Steps" : "Subtasks", previous?.SubtasksPreamble, list.ToString().TrimEnd('\n')));
        }

        var notes = root.SelfAndDescendants().SelectMany(i => i.Notes.Select(n => (Item: i, Note: n))).OrderBy(n => n.Note.At).ToList();
        if (notes.Count > 0 || previous?.NotesPreamble is not null)
        {
            blocks.Add(Section("Notes", previous?.NotesPreamble, string.Join("\n\n", notes.Select(n => RenderNote(n.Item, n.Note, root, tz, previous)))));
        }

        if (root.Attachments.Count > 0 || previous?.AttachmentsPreamble is not null)
        {
            blocks.Add(Section("Attachments", previous?.AttachmentsPreamble, string.Join("\n", root.Attachments.Select(a => "- " + RenderAttachment(a, context, previous)))));
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
        var used = new HashSet<Guid>();
        var needsWrite = !present;

        var id = Guid.TryParse(fm.Scalar("id"), out var parsedId) ? parsedId : Guid.Empty;
        if (id == Guid.Empty)
        {
            id = Guid.NewGuid();
            needsWrite = true;
        }

        used.Add(id);
        var created = VaultText.ParseTime(fm.Scalar("created"), tz, "00:00");
        needsWrite |= created is null;

        // Sections (headings inside code fences don't count).
        string? title = null;
        var details = new List<string>();
        var sections = new Dictionary<string, List<string>> { ["subtasks"] = [], ["notes"] = [], ["attachments"] = [] };
        var seen = new HashSet<string>();
        var current = details;
        var fenced = false;
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fenced = !fenced;
            }
            else if (!fenced && title is null && line.StartsWith("# ", StringComparison.Ordinal))
            {
                title = line[2..].Trim();
                continue;
            }
            else if (!fenced && line.StartsWith("## ", StringComparison.Ordinal))
            {
                var kind = line[3..].Trim().ToLowerInvariant() switch
                {
                    "subtasks" or "steps" or "tasks" or "checklist" => "subtasks",
                    "notes" or "log" => "notes",
                    "attachments" or "files" => "attachments",
                    _ => null,
                };
                if (kind is not null)
                {
                    current = sections[kind];
                    seen.Add(kind);
                    continue;
                }

                current = details;
            }

            current.Add(line);
        }

        if (!seen.Contains("subtasks") && ExtractChecklist(details) is { Count: > 0 } checklist)
        {
            sections["subtasks"] = checklist;
            needsWrite = true;
        }

        if (title is null)
        {
            needsWrite = true;
        }

        var root = new WorkItem(id, string.IsNullOrWhiteSpace(title) ? fileStem : title, ParsePriority(fm.Scalar("priority")), created ?? ctx.Now)
        {
            Details = JoinTrimmed(details),
            Deadline = VaultText.ParseTime(fm.Scalar("due"), tz, VaultText.DefaultDueTime),
            NextActionAt = VaultText.ParseTime(fm.Scalar("scheduled"), tz, VaultText.DefaultScheduledTime),
            StepDelay = VaultText.ParseDuration(fm.Scalar("step-delay")),
        };
        var result = new ParsedTaskFile(root) { Frontmatter = fm, Newline = newline, Bom = bom };

        var status = (fm.Scalar("status") ?? string.Empty).Trim().ToLowerInvariant();
        var done = status is "done" or "completed" or "complete" or "cancelled" or "canceled";
        needsWrite |= status.Length == 0;
        var completed = VaultText.ParseTime(fm.Scalar("completed"), tz, VaultText.DefaultDoneTime);
        if (done)
        {
            root.CompletedAt = completed ?? ctx.Now;
            needsWrite |= completed is null;
        }
        else
        {
            needsWrite |= completed is not null;
        }

        root.TagList.AddRange(fm.List("tags").Select(t => t.TrimStart('#')).Where(t => t.Length > 0));
        root.LabelList.AddRange(fm.List("labels"));
        foreach (var map in fm.Maps("reminders"))
        {
            if (ParseReminder(map, tz, used, ref needsWrite) is { } reminder)
            {
                root.ReminderList.Add(reminder);
            }
        }

        var state = new ParseState(result, ctx, used) { NeedsWrite = needsWrite };
        var ordered = ParseList(sections["subtasks"], root, state);
        root.Sequential = ordered ?? IsTrue(fm.Scalar("sequential"));
        ParseNotes(sections["notes"], root, state);
        ParseAttachments(sections["attachments"], root, state);
        result.NeedsWrite = state.NeedsWrite;
        return result;
    }

    private sealed class ParseState(ParsedTaskFile file, TaskMarkdownContext context, HashSet<Guid> used)
    {
        public ParsedTaskFile File { get; } = file;

        public TaskMarkdownContext Context { get; } = context;

        public HashSet<Guid> Used { get; } = used;

        public bool NeedsWrite { get; set; }

        public Dictionary<string, WorkItem> ByBlockId { get; } = new(StringComparer.Ordinal);

        public Guid NewId(Guid? candidate)
        {
            if (candidate is { } c && c != Guid.Empty && Used.Add(c))
            {
                return c;
            }

            NeedsWrite = true;
            var fresh = Guid.NewGuid();
            Used.Add(fresh);
            return fresh;
        }
    }

    private sealed class Frame(WorkItem item, int indent)
    {
        public WorkItem Item { get; } = item;

        public int Indent { get; } = indent;

        public bool? Ordered { get; set; }

        public List<string> Details { get; } = [];
    }

    /// <summary>A file typed in Obsidian without a "## Subtasks" heading: its first checkbox list is the subtasks.</summary>
    private static List<string>? ExtractChecklist(List<string> details)
    {
        var fenced = false;
        for (var i = 0; i < details.Count; i++)
        {
            var trimmed = details[i].TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            var m = ListItem().Match(details[i]);
            if (fenced || !m.Success || m.Groups["ind"].Length > 0 || !m.Groups["c"].Success)
            {
                continue;
            }

            var end = i + 1;
            while (end < details.Count)
            {
                var line = details[end];
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
        var frames = new List<Frame> { new(root, -1) };
        var created = new List<Frame>();
        var preamble = new List<string>();
        var pendingBlank = 0;
        foreach (var line in lines)
        {
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
                    var item = ParseItem(m.Groups["c"].Success ? m.Groups["c"].Value[0] : ' ', rest, state, isPlain: !m.Groups["c"].Success);
                    item.Parent = parent.Item;
                    parent.Item.ChildList.Add(item);
                    parent.Ordered ??= char.IsDigit(m.Groups["m"].Value[0]);
                    var frame = new Frame(item, indent);
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

            var target = frames[^1];
            var bucket = frames.Count == 1 ? preamble : target.Details;
            if (bucket.Count > 0)
            {
                bucket.AddRange(Enumerable.Repeat(string.Empty, pendingBlank));
            }

            pendingBlank = 0;
            bucket.Add(frames.Count == 1 ? line : StripColumns(line, target.Indent + 4));
        }

        foreach (var frame in created)
        {
            frame.Item.Details = JoinTrimmed(frame.Details);
            frame.Item.Sequential = frame.Ordered ?? frame.Item.Sequential;
        }

        state.File.SubtasksPreamble = JoinTrimmed(preamble);
        return frames[0].Ordered;
    }

    private static WorkItem ParseItem(char check, string rest, ParseState state, bool isPlain)
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
        rest = PriorityToken().Replace(rest, t =>
        {
            priority = t.Groups[1].Value switch
            {
                "🔺" => Priority.Critical,
                "⏫" => Priority.High,
                "🔽" or "⏬" => Priority.Low,
                _ => Priority.Normal,
            };
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

        var id = state.NewId(Guid.TryParse(Str(meta, "id"), out var g) ? g : null);
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

        foreach (var r in meta?["reminders"] as JsonArray ?? [])
        {
            if (r is JsonObject o && VaultText.ParseInstant(Str(o, "at")) is { } at)
            {
                var reminderId = Guid.TryParse(Str(o, "id"), out var rid) ? rid : Guid.NewGuid();
                item.ReminderList.Add(new Reminder(reminderId, at, Str(o, "msg") ?? string.Empty, Str(o, "kind") == "next-action" ? ReminderKind.NextAction : ReminderKind.Manual)
                {
                    NotifiedAt = VaultText.ParseInstant(Str(o, "notified")),
                    DismissedAt = VaultText.ParseInstant(Str(o, "dismissed")),
                });
            }
        }

        var isDone = check is 'x' or 'X' or '-';
        var doneMeta = VaultText.ParseInstant(Str(meta, "done"));
        if (isDone)
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

            var text = JoinTrimmed(body);
            var exact = VaultText.ParseInstant(Str(meta, "at"));
            var at = exact is { } e && (visible is null || VaultText.LocalMinute(e, tz) == VaultText.LocalMinute(visible.Value, tz)) ? e : visible ?? state.Context.Now;
            state.NeedsWrite |= meta is null || exact != at;
            if (text is null)
            {
                state.NeedsWrite = true;
                continue;
            }

            var id = state.NewId(Guid.TryParse(Str(meta, "id"), out var g) ? g : null);
            target.NoteList.Add(new Note(id, at, text, author, sourceUrl, sourceTitle));
        }

        state.File.NotesPreamble = JoinTrimmed(preamble);
    }

    private static void ParseAttachments(List<string> lines, WorkItem root, ParseState state)
    {
        var preamble = new List<string>();
        foreach (var line in lines)
        {
            var m = ListItem().Match(line);
            if (m.Success && !m.Groups["c"].Success && ParseAttachmentLine(m.Groups["rest"].Value, state) is { } attachment)
            {
                root.AttachmentList.Add(attachment);
            }
            else if (line.Trim().Length > 0 || preamble.Count > 0)
            {
                preamble.Add(line);
            }
        }

        state.File.AttachmentsPreamble = JoinTrimmed(preamble);
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
        var wiki = false;
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
            wiki = true;
        }
        else
        {
            return null;
        }

        var id = state.NewId(Guid.TryParse(Str(meta, "id"), out var g) ? g : null);
        var at = VaultText.ParseInstant(Str(meta, "at"));
        state.NeedsWrite |= at is null;
        var attachment = new Attachment(id, name, path, Long(meta, "size") ?? 0, at ?? state.Context.Now, VaultText.ParseActor(Str(meta, "by")));
        if (wiki)
        {
            state.File.WikiLinks.Add(id);
        }

        return attachment;
    }

    private static Reminder? ParseReminder(YamlDotNet.RepresentationModel.YamlMappingNode map, TimeZoneInfo tz, HashSet<Guid> used, ref bool needsWrite)
    {
        var at = VaultText.ParseTime(Frontmatter.Get(map, "at"), tz, "09:00");
        if (at is null)
        {
            return null;
        }

        var hasId = Guid.TryParse(Frontmatter.Get(map, "id"), out var g) && used.Add(g);
        needsWrite |= !hasId;
        var kind = (Frontmatter.Get(map, "kind") ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Equals("nextaction", StringComparison.OrdinalIgnoreCase)
            ? ReminderKind.NextAction
            : ReminderKind.Manual;
        return new Reminder(hasId ? g : Guid.NewGuid(), at.Value, Frontmatter.Get(map, "message") ?? string.Empty, kind)
        {
            NotifiedAt = VaultText.ParseTime(Frontmatter.Get(map, "notified"), tz, "00:00"),
            DismissedAt = VaultText.ParseTime(Frontmatter.Get(map, "dismissed"), tz, "00:00"),
        };
    }

    // ---- Rendering ----------------------------------------------------------------------

    private static void RenderFrontmatter(StringBuilder sb, WorkItem root, TimeZoneInfo tz, ParsedTaskFile? previous)
    {
        foreach (var (key, raw) in previous?.Frontmatter.Blocks ?? [])
        {
            if (key is null)
            {
                sb.Append(raw);
            }
        }

        sb.Append("id: ").Append(root.Id).Append('\n');
        sb.Append("status: ").Append(root.IsDone ? "done" : "open").Append('\n');
        if (root.Priority != Priority.Normal)
        {
            sb.Append("priority: ").Append(root.Priority.ToString().ToLowerInvariant()).Append('\n');
        }

        sb.Append("created: ").Append(VaultText.Utc(root.CreatedAt)).Append('\n');
        if (root.CompletedAt is { } completed)
        {
            sb.Append("completed: ").Append(VaultText.Utc(completed)).Append('\n');
        }

        if (root.Deadline is { } due)
        {
            sb.Append("due: ").Append(VaultText.LocalDateTime(due, tz)).Append('\n');
        }

        if (root.NextActionAt is { } next)
        {
            sb.Append("scheduled: ").Append(VaultText.LocalDateTime(next, tz)).Append('\n');
        }

        if (root.Sequential)
        {
            sb.Append("sequential: true\n");
        }

        if (root.StepDelay is { } delay)
        {
            sb.Append("step-delay: ").Append(VaultText.Duration(delay)).Append('\n');
        }

        AppendList(sb, "tags", root.Tags);
        AppendList(sb, "labels", root.Labels);
        if (root.Reminders.Count > 0)
        {
            sb.Append("reminders:\n");
            foreach (var r in root.Reminders)
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
        }

        foreach (var (key, raw) in previous?.Frontmatter.Blocks ?? [])
        {
            if (key is not null && !OwnedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(raw);
            }
        }
    }

    private static void AppendList(StringBuilder sb, string key, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        sb.Append(key).Append(":\n");
        foreach (var value in values)
        {
            sb.Append("  - ").Append(VaultText.YamlScalar(value)).Append('\n');
        }
    }

    private static void RenderItems(StringBuilder sb, IReadOnlyList<WorkItem> items, bool ordered, int depth, TaskMarkdownContext ctx, ParsedTaskFile? previous)
    {
        var indent = new string('\t', depth);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            sb.Append(indent).Append(ordered ? $"{i + 1}." : "-").Append(" [").Append(item.IsDone ? 'x' : ' ').Append("] ")
                .Append(ItemLine(item, ctx.TimeZone, previous)).Append('\n');
            if (!string.IsNullOrEmpty(item.Details))
            {
                foreach (var line in item.Details.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                {
                    sb.Append(line.Length == 0 ? string.Empty : indent + "\t" + line).Append('\n');
                }
            }

            foreach (var attachment in item.Attachments)
            {
                sb.Append(indent).Append("\t- 📎 ").Append(RenderAttachment(attachment, ctx, previous)).Append('\n');
            }

            RenderItems(sb, item.Children, item.Sequential, depth + 1, ctx, previous);
        }
    }

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

        sb.Append(item.Priority switch
        {
            Priority.Critical => " 🔺",
            Priority.High => " ⏫",
            Priority.Low => " 🔽",
            _ => string.Empty,
        });
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
        var link = previous?.WikiLinks.Contains(attachment.Id) == true
            ? $"![[{attachment.Path}]]"
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

    private static string? JoinTrimmed(List<string> lines)
    {
        var start = lines.FindIndex(l => l.Trim().Length > 0);
        if (start < 0)
        {
            return null;
        }

        var end = lines.FindLastIndex(l => l.Trim().Length > 0);
        return string.Join('\n', lines.Skip(start).Take(end - start + 1).Select(l => l.TrimEnd()));
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
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
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
