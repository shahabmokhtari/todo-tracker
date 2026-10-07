using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TodoTracker.Core.Sync;

public enum SyncOpKind
{
    /// <summary>Write <see cref="SyncOp.Content"/> to <see cref="SyncOp.Path"/>; <see cref="SyncOp.Expected"/> null means the file must not exist yet.</summary>
    Write,

    /// <summary>Move the file at <see cref="SyncOp.Path"/> to <see cref="SyncOp.To"/>.</summary>
    Move,

    /// <summary>Move the file to the vault's trash.</summary>
    Delete,

    /// <summary>Append these activity lines.</summary>
    AppendActivity,

    /// <summary>Replace the tabs/labels/order settings.</summary>
    SetConfig,

    /// <summary>Replace the manual Now order (a JSON array of task ids).</summary>
    SetOrder,
}

/// <summary>One change to make to the vault; <see cref="Expected"/> is the hash the file must still have (else the sync starts over).</summary>
public sealed record SyncOp(SyncOpKind Kind, string Path, string? Expected, byte[]? Content = null, string? To = null);

/// <summary>
/// Both devices changed the same lines; both versions' lines were kept (the result is the same on both devices).
/// <paramref name="Result"/> is the hash of what was written (choosing a side later only applies if the file is still that).
/// </summary>
public sealed record SyncConflict(string Key, string Path, string Peer, string PeerName, string Mine, string Theirs, string? Base, string Result);

public sealed record SyncPlan(IReadOnlyList<SyncOp> Ops, IReadOnlyList<SyncConflict> Conflicts);

/// <summary>What the planner may ask beyond the two sides.</summary>
public sealed record SyncPlanOptions
{
    /// <summary>Whether some device recorded deleting exactly this version (so it isn't brought back).</summary>
    public Func<SyncEntry, bool> Deleted { get; init; } = _ => false;

    /// <summary>Whether merged text is still a valid task file (else both versions are kept whole).</summary>
    public Func<string, string, bool> IsValidTask { get; init; } = (_, _) => true;
}

/// <summary>
/// Works out how to bring a peer's changes into this vault: a three-way merge per entry against what both devices last
/// agreed on. Whenever a choice has to be made, the device whose id sorts first wins, so both devices make the same
/// choice and end up with the same files.
/// </summary>
public static partial class SyncPlanner
{
    public static SyncPlan Plan(SyncSide local, SyncSide peer, IReadOnlyDictionary<string, SyncEntry> @base, Func<string, byte[]?> readBase, SyncPlanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(@base);
        ArgumentNullException.ThrowIfNull(readBase);
        var ctx = new Context(local, peer, @base, readBase, options ?? new SyncPlanOptions());
        var files = new List<(string Key, SyncEntry? L, SyncEntry P, SyncEntry? B)>();
        var keys = local.Entries.Keys.Union(peer.Entries.Keys).Union(@base.Keys).Order(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var l = local.Entries.GetValueOrDefault(key);
            var p = peer.Entries.GetValueOrDefault(key);
            var b = @base.GetValueOrDefault(key);
            var isFile = SyncKeys.KindOf(key) is SyncKind.Task or SyncKind.Rich or SyncKind.File;
            if (p is null || (l is not null && l.Hash == p.Hash && l.Path == p.Path))
            {
                // The peer doesn't have it (or has exactly ours): a peer delete of something unchanged here matters,
                // and so does a delete some device recorded of exactly this version.
                if (p is null && l is not null && isFile && ((b is not null && l.Hash == b.Hash && l.Path == b.Path) || ctx.Options.Deleted(l)))
                {
                    ctx.Ops.Add(new SyncOp(SyncOpKind.Delete, l.Path, l.Hash));
                }

                continue;
            }

            switch (SyncKeys.KindOf(key))
            {
                case SyncKind.Activity:
                    MergeActivity(ctx, l, p);
                    break;
                case SyncKind.Order:
                    MergeJson(ctx, SyncOpKind.SetOrder, l, p, b, indented: false);
                    break;
                case SyncKind.Config:
                    MergeJson(ctx, SyncOpKind.SetConfig, l, p, b, indented: true);
                    break;
                default:
                    files.Add((key, l, p, b));
                    break;
            }
        }

        // Paths that are deleted in this plan are free for new files (a file renamed only in letter case, say).
        foreach (var delete in ctx.Ops.Where(o => o.Kind == SyncOpKind.Delete))
        {
            ctx.Taken.Remove(delete.Path);
        }

        // Where things live first (so moves never land on another file), then what they contain.
        var paths = PlanMoves(ctx, files.Where(f => f.L is not null).Select(f => (f.Key, f.L!, f.P, f.B)).ToList());
        foreach (var (key, l, p, b) in files)
        {
            MergeFile(ctx, key, l, p, b, l is null ? null : paths[key]);
        }

        // Moves, then deletes (to the trash, freeing their paths), then writes; the activity log last, so it never
        // describes a change that didn't happen.
        var ordered = ctx.Moves
            .Concat(ctx.Ops.Where(o => o.Kind == SyncOpKind.Delete))
            .Concat(ctx.Ops.Where(o => o.Kind == SyncOpKind.Write))
            .Concat(ctx.Ops.Where(o => o.Kind is SyncOpKind.SetConfig or SyncOpKind.SetOrder))
            .Concat(ctx.Ops.Where(o => o.Kind == SyncOpKind.AppendActivity))
            .ToList();
        return new SyncPlan(ordered, ctx.Conflicts);
    }

    /// <summary>What both devices now agree on (same content, same place); everything else keeps its old base.</summary>
    public static Dictionary<string, SyncEntry> NextBase(IReadOnlyDictionary<string, SyncEntry> local, IReadOnlyDictionary<string, SyncEntry> peer, IReadOnlyDictionary<string, SyncEntry> oldBase)
    {
        var next = new Dictionary<string, SyncEntry>(StringComparer.Ordinal);
        foreach (var key in local.Keys.Union(peer.Keys).Union(oldBase.Keys))
        {
            var l = local.GetValueOrDefault(key);
            var p = peer.GetValueOrDefault(key);
            if (l is not null && p is not null && l.Hash == p.Hash && l.Path == p.Path)
            {
                next[key] = p;
            }
            else if ((l is not null || p is not null) && oldBase.TryGetValue(key, out var old))
            {
                next[key] = old;
            }
        }

        return next;
    }

    private sealed class Context(SyncSide local, SyncSide peer, IReadOnlyDictionary<string, SyncEntry> @base, Func<string, byte[]?> readBase, SyncPlanOptions options)
    {
        public SyncSide Local { get; } = local;

        public SyncSide Peer { get; } = peer;

        public IReadOnlyDictionary<string, SyncEntry> Base { get; } = @base;

        public Func<string, byte[]?> ReadBase { get; } = readBase;

        public SyncPlanOptions Options { get; } = options;

        public List<SyncOp> Ops { get; } = [];

        public List<SyncOp> Moves { get; } = [];

        public List<SyncConflict> Conflicts { get; } = [];

        /// <summary>Paths in use here (and planned), to never write over another entry's file.</summary>
        public HashSet<string> Taken { get; } = new(local.Entries.Values.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);

        public bool LocalWins { get; } = string.CompareOrdinal(local.Device, peer.Device) < 0;

        public string LoserName => LocalWins ? Peer.Name : Local.Name;

        public SyncEntry? LocalAt(string path) => Local.Entries.Values.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));

        public string FreePath(string path)
        {
            var dir = path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : string.Empty;
            var ext = System.IO.Path.GetExtension(path);
            var stem = path[dir.Length..^ext.Length];
            var candidate = path;
            for (var n = 2; Taken.Contains(candidate); n++)
            {
                candidate = $"{dir}{stem} {n}{ext}";
            }

            Taken.Add(candidate);
            return candidate;
        }
    }

    /// <summary>
    /// Decides where each file both sides have lives: a move on one side wins, moves on both go to the winner's
    /// choice. A move onto a file that stays put is left for later (the next sync, once that file moved or went);
    /// files that trade places go through a temporary name. Returns each key's final path.
    /// </summary>
    private static Dictionary<string, string> PlanMoves(Context ctx, List<(string Key, SyncEntry L, SyncEntry P, SyncEntry? B)> both)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var wanted = new List<(string Key, SyncEntry L, string Target)>();
        foreach (var (key, l, p, b) in both)
        {
            paths[key] = l.Path;
            if (l.Path != p.Path)
            {
                var target = b is not null && p.Path == b.Path ? l.Path
                    : b is not null && l.Path == b.Path ? p.Path
                    : ctx.LocalWins ? l.Path : p.Path;
                if (target != l.Path)
                {
                    wanted.Add((key, l, target));
                }
            }
        }

        // A move needs its target free, or freed by a move that also happens; drop blocked moves until none are.
        var active = wanted;
        while (true)
        {
            var leavingNow = new HashSet<string>(active.Select(w => w.L.Path), StringComparer.OrdinalIgnoreCase);
            var claimedNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keep = active.Where(w => (string.Equals(w.Target, w.L.Path, StringComparison.OrdinalIgnoreCase) || ctx.LocalAt(w.Target) is null || leavingNow.Contains(w.Target)) && claimedNow.Add(w.Target)).ToList();
            if (keep.Count == active.Count)
            {
                break;
            }

            active = keep;
        }

        var moves = new List<(SyncEntry L, string Target, bool Swap)>();
        foreach (var (key, l, target) in active)
        {
            var caseOnly = string.Equals(target, l.Path, StringComparison.OrdinalIgnoreCase);
            paths[key] = target;
            ctx.Taken.Add(target);
            moves.Add((l, target, !caseOnly && ctx.LocalAt(target) is not null));
        }

        // Files that trade places step aside first.
        foreach (var (l, target, swap) in moves)
        {
            ctx.Moves.Add(new SyncOp(SyncOpKind.Move, l.Path, l.Hash, To: swap ? Aside(l) : target));
        }

        foreach (var (l, target, _) in moves.Where(m => m.Swap))
        {
            ctx.Moves.Add(new SyncOp(SyncOpKind.Move, Aside(l), l.Hash, To: target));
        }

        return paths;

        static string Aside(SyncEntry l)
        {
            var ext = System.IO.Path.GetExtension(l.Path);
            return $"{l.Path[..^ext.Length]} (moving {l.Hash[..8]}){ext}";
        }
    }

    private static void MergeFile(Context ctx, string key, SyncEntry? l, SyncEntry p, SyncEntry? b, string? path2)
    {
        if (l is null)
        {
            // Deleted here and untouched there: stays deleted; so does a version some device deleted. Otherwise it's
            // new here (or was changed there: edits win).
            if ((b is not null && p.Hash == b.Hash && p.Path == b.Path) || (b is null && ctx.Options.Deleted(p)))
            {
                return;
            }

            var path = ctx.Taken.Contains(p.Path) ? ctx.FreePath(p.Path) : Take(ctx, p.Path);
            ctx.Ops.Add(new SyncOp(SyncOpKind.Write, path, null, ctx.Peer.Read(p)));
            return;
        }

        var current = l.Path != path2 ? l with { Path = path2! } : l;
        if (l.Hash == p.Hash || (b is not null && p.Hash == b.Hash))
        {
            return;
        }

        if (b is not null && l.Hash == b.Hash)
        {
            ctx.Ops.Add(new SyncOp(SyncOpKind.Write, current.Path, l.Hash, ctx.Peer.Read(p)));
            return;
        }

        var mine = ctx.Local.Read(l);
        var theirs = ctx.Peer.Read(p);
        var (winner, loser) = ctx.LocalWins ? (mine, theirs) : (theirs, mine);
        var kind = SyncKeys.KindOf(key);
        var baseBytes = b is null ? null : ctx.ReadBase(b.Hash);
        if (kind is SyncKind.Task or SyncKind.Rich && baseBytes is not null)
        {
            var merged = TextMerge.Merge(Encoding.UTF8.GetString(baseBytes), Encoding.UTF8.GetString(winner), Encoding.UTF8.GetString(loser), kind == SyncKind.Task ? TaskLineKeys : null);

            // A merge that isn't a valid task any more (say, both changed the same frontmatter) isn't written: both
            // versions are kept whole instead.
            if (kind != SyncKind.Task || ctx.Options.IsValidTask(current.Path, merged.Text))
            {
                var text = Encoding.UTF8.GetBytes(merged.Text);
                if (merged.Conflicted)
                {
                    ctx.Conflicts.Add(new SyncConflict(key, current.Path, ctx.Peer.Device, ctx.Peer.Name, l.Hash, p.Hash, b?.Hash, SyncHash.Of(text)));
                }

                if (SyncHash.Of(text) != l.Hash)
                {
                    ctx.Ops.Add(new SyncOp(SyncOpKind.Write, current.Path, l.Hash, text));
                }

                return;
            }

            ctx.Conflicts.Add(new SyncConflict(key, current.Path, ctx.Peer.Device, ctx.Peer.Name, l.Hash, p.Hash, b?.Hash, SyncHash.Of(winner)));
        }

        // Nothing to merge against (or not text, or no valid merge): the winner keeps the file, the other version is
        // kept next to it, the same on both devices.
        var copyPath = CopyPath(current.Path, ctx.LoserName);
        var copy = kind == SyncKind.Task ? Encoding.UTF8.GetBytes(MarkTitle(Encoding.UTF8.GetString(loser), ctx.LoserName)) : loser;
        var copyHash = SyncHash.Of(copy);
        var existing = ctx.LocalAt(copyPath);
        var planned = ctx.Ops.Any(o => o.Kind == SyncOpKind.Write && string.Equals(o.Path, copyPath, StringComparison.OrdinalIgnoreCase) && SyncHash.Of(o.Content!) == copyHash);
        if (!planned && (existing is null || existing.Hash != copyHash))
        {
            ctx.Ops.Add(new SyncOp(SyncOpKind.Write, existing is null && !ctx.Taken.Contains(copyPath) ? Take(ctx, copyPath) : ctx.FreePath(copyPath), null, copy));
        }

        if (!ctx.LocalWins)
        {
            ctx.Ops.Add(new SyncOp(SyncOpKind.Write, current.Path, l.Hash, winner));
        }
    }

    private static string Take(Context ctx, string path)
    {
        ctx.Taken.Add(path);
        return path;
    }

    private static string CopyPath(string path, string device)
    {
        // The name comes from another device: only characters every file system takes.
        var safe = new string([.. device.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '\'' or '(' or ')')]).Trim();
        var ext = System.IO.Path.GetExtension(path);
        return $"{path[..^ext.Length]} (from {(safe.Length > 0 ? safe[..Math.Min(40, safe.Length)].Trim() : "another device")}){ext}";
    }

    /// <summary>"# Title" becomes "# Title (from Device)" so the copy is told apart in every list.</summary>
    private static string MarkTitle(string text, string device)
    {
        var match = Heading().Match(text);
        return match.Success ? text.Insert(match.Groups[1].Index + match.Groups[1].Length, $" (from {device})") : text;
    }

    [GeneratedRegex(@"^# (.*?)\r?$", RegexOptions.Multiline)]
    private static partial Regex Heading();

    /// <summary>A task file's line identities: frontmatter keys inside the frontmatter, steps' block ids (<c>^x7k2m9</c>) below.</summary>
    public static string?[] TaskLineKeys(IReadOnlyList<string> lines)
    {
        var keys = new string?[lines.Count];
        var inFrontmatter = lines.Count > 0 && lines[0].TrimEnd('\r', '\n') == "---";
        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i].TrimEnd('\r', '\n');
            if (inFrontmatter && i > 0 && text == "---")
            {
                inFrontmatter = false;
                continue;
            }

            if (inFrontmatter && i > 0)
            {
                keys[i] = FrontmatterKey().Match(text) is { Success: true } key ? key.Groups[1].Value + ":" : null;
            }
            else if (!inFrontmatter && BlockId().Match(text) is { Success: true } block)
            {
                keys[i] = "^" + block.Groups[1].Value;
            }
            else if (!inFrontmatter && HiddenId().Match(text) is { Success: true } hidden)
            {
                // Time and attachment lines carry their id: two devices adding different ones never collide.
                keys[i] = "id:" + hidden.Groups[1].Value;
            }
        }

        return keys;
    }

    [GeneratedRegex(@"\s\^([A-Za-z0-9-]+)(?:\s+%%.*%%)?\s*$")]
    private static partial Regex BlockId();

    [GeneratedRegex(@"^[-*+] .*%%\{""id"":""([0-9a-fA-F-]{36})""")]
    private static partial Regex HiddenId();

    [GeneratedRegex(@"^([A-Za-z][\w-]*):(?:\s|$)")]
    private static partial Regex FrontmatterKey();

    private static void MergeActivity(Context ctx, SyncEntry? l, SyncEntry p)
    {
        var known = l is null ? [] : Lines(ctx.Local.Read(l)).ToHashSet(StringComparer.Ordinal);
        var added = Lines(ctx.Peer.Read(p)).Where(known.Add).ToList();
        if (added.Count > 0)
        {
            ctx.Ops.Add(new SyncOp(SyncOpKind.AppendActivity, SyncKeys.ActivityPath, null, Encoding.UTF8.GetBytes(string.Concat(added.Select(a => a + "\n")))));
        }

        static IEnumerable<string> Lines(byte[] bytes) => Encoding.UTF8.GetString(bytes).Split('\n').Select(s => s.TrimEnd('\r')).Where(s => s.Length > 0);
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static void MergeJson(Context ctx, SyncOpKind kind, SyncEntry? l, SyncEntry p, SyncEntry? b, bool indented)
    {
        byte[] content;
        if (l is null || (b is not null && l.Hash == b.Hash))
        {
            content = ctx.Peer.Read(p);
        }
        else if (b is not null && p.Hash == b.Hash)
        {
            return;
        }
        else
        {
            var mine = Parse(ctx.Local.Read(l));
            var theirs = Parse(ctx.Peer.Read(p));
            if (mine is null || theirs is null)
            {
                return;
            }

            var baseBytes = b is null ? null : ctx.ReadBase(b.Hash);
            var old = baseBytes is null ? null : Parse(baseBytes);
            var merged = ctx.LocalWins ? MergeNode(old, mine, theirs) : MergeNode(old, theirs, mine);
            if (kind == SyncOpKind.SetConfig)
            {
                OneTabPerFolder(merged);
            }

            var text = merged?.ToJsonString(indented ? Indented : JsonSerializerOptions.Default) ?? "null";
            content = Encoding.UTF8.GetBytes(indented ? text + "\n" : text);
        }

        if (l is null || SyncHash.Of(content) != l.Hash)
        {
            ctx.Ops.Add(new SyncOp(kind, kind == SyncOpKind.SetOrder ? SyncKeys.OrderPath : SyncKeys.ConfigPath, l?.Hash, content));
        }

        static JsonNode? Parse(byte[] bytes)
        {
            try
            {
                return JsonNode.Parse(bytes);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>Three-way merge of JSON: objects by property, lists of objects by "id" (or "name"), plain lists as ordered sets.</summary>
    internal static JsonNode? MergeNode(JsonNode? b, JsonNode? winner, JsonNode? loser)
    {
        if (JsonNode.DeepEquals(winner, loser) || JsonNode.DeepEquals(b, loser))
        {
            return winner?.DeepClone();
        }

        if (JsonNode.DeepEquals(b, winner))
        {
            return loser?.DeepClone();
        }

        if (winner is JsonObject wo && loser is JsonObject lo)
        {
            var bo = b as JsonObject;
            var result = new JsonObject();
            foreach (var name in wo.Select(x => x.Key).Concat(lo.Select(x => x.Key)).Distinct(StringComparer.Ordinal))
            {
                var bv = bo?[name];
                var inBase = bo?.ContainsKey(name) == true;
                if (wo.TryGetPropertyValue(name, out var wv) && lo.TryGetPropertyValue(name, out var lv))
                {
                    result[name] = MergeNode(bv, wv, lv);
                }
                else if (wo.TryGetPropertyValue(name, out wv))
                {
                    // Removed on one side: gone unless the other side changed it since.
                    if (!inBase || !JsonNode.DeepEquals(bv, wv))
                    {
                        result[name] = wv?.DeepClone();
                    }
                }
                else if (!inBase || !JsonNode.DeepEquals(bv, lo[name]))
                {
                    result[name] = lo[name]?.DeepClone();
                }
            }

            return result;
        }

        if (winner is JsonArray wa && loser is JsonArray la)
        {
            var keyName = KeyNames.FirstOrDefault(k => wa.Concat(la).All(x => x is JsonObject o && o[k] is JsonValue));
            return keyName is null ? MergeList(b as JsonArray, wa, la) : MergeKeyed(b as JsonArray, wa, la, keyName);
        }

        return winner?.DeepClone();
    }

    /// <summary>Two devices that each made a "Work" tab have one "Work" folder: the first tab for a folder stays.</summary>
    private static void OneTabPerFolder(JsonNode? config)
    {
        if (config?["groups"] is not JsonArray groups)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups.ToList())
        {
            if (group?["folder"] is JsonValue folder && folder.TryGetValue<string>(out var name) && !seen.Add(name))
            {
                groups.Remove(group);
            }
        }
    }

    private static readonly string[] KeyNames = ["id", "name"];

    private static JsonArray MergeList(JsonArray? b, JsonArray winner, JsonArray loser)
    {
        var removed = (b ?? []).Where(x => !winner.Any(w => JsonNode.DeepEquals(w, x)) || !loser.Any(l => JsonNode.DeepEquals(l, x))).ToList();
        var result = new JsonArray();
        foreach (var item in winner.Concat(loser.Where(l => !winner.Any(w => JsonNode.DeepEquals(w, l)))))
        {
            if (!removed.Any(r => JsonNode.DeepEquals(r, item)) && !result.Any(r => JsonNode.DeepEquals(r, item)))
            {
                result.Add(item?.DeepClone());
            }
        }

        return result;
    }

    private static JsonArray MergeKeyed(JsonArray? b, JsonArray winner, JsonArray loser, string keyName)
    {
        static string Key(JsonNode? node, string keyName) => node![keyName]!.ToJsonString();
        var old = (b ?? []).Where(x => x is JsonObject o && o[keyName] is JsonValue).ToDictionary(x => Key(x, keyName));
        var w = winner.ToDictionary(x => Key(x, keyName));
        var l = loser.ToDictionary(x => Key(x, keyName));
        var result = new JsonArray();
        foreach (var key in w.Keys.Concat(l.Keys.Where(k => !w.ContainsKey(k))))
        {
            var bv = old.GetValueOrDefault(key);
            JsonNode? merged = (w.TryGetValue(key, out var wv), l.TryGetValue(key, out var lv)) switch
            {
                (true, true) => MergeNode(bv, wv, lv),

                // Removed on one side: gone unless the other side changed it since.
                (true, false) => bv is not null && JsonNode.DeepEquals(bv, wv) ? null : wv!.DeepClone(),
                _ => bv is not null && JsonNode.DeepEquals(bv, lv) ? null : lv!.DeepClone(),
            };
            if (merged is not null)
            {
                result.Add(merged);
            }
        }

        return result;
    }
}
