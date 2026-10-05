using System.Text.RegularExpressions;

namespace TodoTracker.Core;

/// <summary>Tags, labels, attachments, and moving tasks around the hierarchy.</summary>
public sealed partial class TaskBoard
{
    public const int MaxTags = 20;
    public const int MaxTagLength = 50;
    public const int MaxLabelLength = 40;

    /// <summary>Accessible on light and dark backgrounds; new labels take the next unused one.</summary>
    internal static readonly string[] LabelPalette = ["#7c3aed", "#0ea5e9", "#10b981", "#f59e0b", "#ef4444", "#ec4899", "#14b8a6", "#64748b"];

    internal readonly List<LabelDefinition> LabelDefinitionList = [];

    public IReadOnlyList<LabelDefinition> Labels => LabelDefinitionList;

    public void SetTags(Guid id, IEnumerable<string> tags, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        var clean = NormalizeTags(tags);
        item.TagList.Clear();
        item.TagList.AddRange(clean);
        Log(now, id, ActivityKind.Updated, clean.Count == 0 ? $"Cleared tags on \"{item.Title}\"" : $"Tags on \"{item.Title}\": {string.Join(' ', clean.Select(t => "#" + t))}", actor);
    }

    public void SetLabels(Guid id, IEnumerable<string> labels, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        var names = ValidateLabelNames(labels);
        var resolved = names.Select(n => EnsureLabel(n).Name).ToList();
        item.LabelList.Clear();
        item.LabelList.AddRange(resolved);
        Log(now, id, ActivityKind.Updated, resolved.Count == 0 ? $"Cleared labels on \"{item.Title}\"" : $"Labels on \"{item.Title}\": {string.Join(", ", resolved)}", actor);
    }

    public LabelDefinition DefineLabel(string name, string? color, Actor actor, DateTimeOffset now)
    {
        var clean = ValidateLabelNames([name]).Single();
        if (color is not null)
        {
            ValidateColor(color);
        }

        if (FindLabel(clean) is not null)
        {
            throw new ArgumentException($"A label named \"{clean}\" already exists.", nameof(name));
        }

        var label = color is null ? EnsureLabel(clean) : new LabelDefinition(clean, color.ToLowerInvariant());
        if (color is not null)
        {
            LabelDefinitionList.Add(label);
        }

        Log(now, Guid.Empty, ActivityKind.LabelChanged, $"Added label \"{clean}\"", actor);
        return label;
    }

    public void UpdateLabel(string name, string newName, string color, Actor actor, DateTimeOffset now)
    {
        var label = FindLabel(name) ?? throw new TaskNotFoundException($"Label \"{name}\" was not found.");
        var clean = ValidateLabelNames([newName]).Single();
        ValidateColor(color);
        if (FindLabel(clean) is { } other && other != label)
        {
            throw new ArgumentException($"A label named \"{clean}\" already exists.", nameof(newName));
        }

        var old = label.Name;
        foreach (var item in AllItems())
        {
            var index = item.LabelList.FindIndex(l => string.Equals(l, old, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                item.LabelList[index] = clean;
            }
        }

        label.Name = clean;
        label.Color = color.ToLowerInvariant();
        Log(now, Guid.Empty, ActivityKind.LabelChanged, old == clean ? $"Updated label \"{clean}\"" : $"Renamed label \"{old}\" to \"{clean}\"", actor);
    }

    public void DeleteLabel(string name, Actor actor, DateTimeOffset now)
    {
        var label = FindLabel(name) ?? throw new TaskNotFoundException($"Label \"{name}\" was not found.");
        foreach (var item in AllItems())
        {
            item.LabelList.RemoveAll(l => string.Equals(l, label.Name, StringComparison.OrdinalIgnoreCase));
        }

        LabelDefinitionList.Remove(label);
        Log(now, Guid.Empty, ActivityKind.LabelChanged, $"Deleted label \"{label.Name}\"", actor);
    }

    public LabelDefinition? FindLabel(string name) =>
        LabelDefinitionList.Find(l => string.Equals(l.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    public Attachment AddAttachment(Guid id, string fileName, string path, long size, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        var name = ValidateFileName(fileName);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("An attachment path is required.", nameof(path));
        }

        var attachment = new Attachment(Guid.NewGuid(), name, path.Replace('\\', '/'), Math.Max(0, size), now, actor);
        item.AttachmentList.Add(attachment);
        Log(now, id, ActivityKind.AttachmentAdded, $"Attached \"{name}\" to \"{item.Title}\"", actor);
        return attachment;
    }

    public void RemoveAttachment(Guid id, Guid attachmentId, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        var attachment = item.AttachmentList.Find(a => a.Id == attachmentId) ?? throw new TaskNotFoundException($"Attachment {attachmentId} was not found.");
        item.AttachmentList.Remove(attachment);
        Log(now, id, ActivityKind.AttachmentRemoved, $"Removed \"{attachment.FileName}\" from \"{item.Title}\"", actor);
    }

    /// <summary>
    /// Moves a task under <paramref name="parentId"/> (or to the top level, in <paramref name="groupId"/> or its current
    /// group) at <paramref name="index"/> (null = last). Subtasks move with it.
    /// </summary>
    public void Move(Guid id, Guid? parentId, int? index, Guid? groupId, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        var parent = parentId is { } p ? Get(p) : null;
        if (parent is not null && (parent == item || parent.Ancestors().Contains(item)))
        {
            throw new InvalidOperationException($"\"{item.Title}\" can't move into itself or one of its subtasks.");
        }

        var group = parent is null ? GetGroup(groupId ?? item.GroupId) : null;
        var siblings = parent?.ChildList ?? RootList;
        (item.Parent?.ChildList ?? RootList).Remove(item);
        siblings.Insert(Math.Clamp(index ?? siblings.Count, 0, siblings.Count), item);
        item.Parent = parent;
        if (group is not null)
        {
            item.OwnGroupId = group.Id;
        }

        Log(now, id, ActivityKind.Moved, parent is null ? $"Moved \"{item.Title}\" to {group!.Name}" : $"Moved \"{item.Title}\" under \"{parent.Title}\"", actor);
    }

    internal static List<string> NormalizeTags(IEnumerable<string>? tags)
    {
        var result = new List<string>();
        foreach (var raw in tags ?? [])
        {
            var tag = (raw ?? string.Empty).Trim().TrimStart('#');
            if (tag.Length == 0 || tag.Length > MaxTagLength || !TagPattern().IsMatch(tag) || tag.All(c => char.IsDigit(c) || c == '/'))
            {
                throw new ArgumentException($"\"{raw}\" isn't a valid tag. Use letters, numbers, - _ and / (not only digits), up to {MaxTagLength} characters.", nameof(tags));
            }

            if (!result.Exists(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(tag);
            }
        }

        return result.Count > MaxTags ? throw new ArgumentException($"At most {MaxTags} tags per task.", nameof(tags)) : result;
    }

    internal static List<string> ValidateLabelNames(IEnumerable<string>? labels)
    {
        var result = new List<string>();
        foreach (var raw in labels ?? [])
        {
            var name = (raw ?? string.Empty).Trim();
            if (name.Length == 0 || name.Length > MaxLabelLength || name.Any(c => char.IsControl(c) || c is ',' or '[' or ']' or '"'))
            {
                throw new ArgumentException($"\"{raw}\" isn't a valid label. Use up to {MaxLabelLength} characters without commas, brackets, or quotes.", nameof(labels));
            }

            if (!result.Exists(l => string.Equals(l, name, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(name);
            }
        }

        return result.Count > MaxTags ? throw new ArgumentException($"At most {MaxTags} labels per task.", nameof(labels)) : result;
    }

    /// <summary>Finds the label (case-insensitive) or defines it with the next palette color.</summary>
    internal LabelDefinition EnsureLabel(string name)
    {
        if (FindLabel(name) is { } existing)
        {
            return existing;
        }

        var used = LabelDefinitionList.Select(l => l.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var color = LabelPalette.FirstOrDefault(c => !used.Contains(c)) ?? LabelPalette[LabelDefinitionList.Count % LabelPalette.Length];
        var label = new LabelDefinition(name, color);
        LabelDefinitionList.Add(label);
        return label;
    }

    internal static string ValidateFileName(string? fileName)
    {
        var name = (fileName ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > 200 || name is "." or ".." || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name.Any(char.IsControl))
        {
            throw new ArgumentException($"\"{fileName}\" isn't a valid file name.", nameof(fileName));
        }

        return name;
    }

    private static void ValidateColor(string? color)
    {
        if (color is null || !HexColor().IsMatch(color))
        {
            throw new ArgumentException("Colors must be #rrggbb.", nameof(color));
        }
    }

    [GeneratedRegex(@"^[\p{L}\p{N}_\-/]+$")]
    private static partial Regex TagPattern();
}
