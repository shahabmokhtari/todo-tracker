namespace TodoTracker.Core.Sync;

public sealed record TextMergeResult(string Text, bool Conflicted);

/// <summary>What to do where both sides changed the same lines differently.</summary>
public enum ConflictPolicy
{
    /// <summary>Keep both sides' lines (the first side's first): nothing typed is lost.</summary>
    Both,

    /// <summary>Keep only the first side's lines there (everything else still merges).</summary>
    First,

    /// <summary>Keep only the second side's lines there.</summary>
    Second,
}

/// <summary>
/// Line-based three-way merge (diff3). Changes to different lines are combined; where both sides changed the same
/// lines differently, both versions' lines are kept (the first side's first), so nothing typed is ever lost and
/// the result is the same on every device as long as callers order the sides the same way.
/// </summary>
public static class TextMerge
{
    /// <param name="lineKeys">Gives each line of a file its identity (or null), e.g. a step's block id: keyed lines merge one by one.</param>
    public static TextMergeResult Merge(string @base, string first, string second, Func<IReadOnlyList<string>, string?[]>? lineKeys = null, ConflictPolicy policy = ConflictPolicy.Both)
    {
        var o = Split(@base);
        var a = Split(first);
        var b = Split(second);
        var ko = lineKeys?.Invoke(o);
        var ka = lineKeys?.Invoke(a);
        var kb = lineKeys?.Invoke(b);
        var ma = Match(o, a);
        var mb = Match(o, b);
        var output = new List<string>();
        var conflicted = false;
        int io = 0, ia = 0, ib = 0;
        while (true)
        {
            while (io < o.Length && ma[io] == ia && mb[io] == ib)
            {
                output.Add(o[io]);
                io++;
                ia++;
                ib++;
            }

            if (io >= o.Length && ia >= a.Length && ib >= b.Length)
            {
                break;
            }

            // The next base line both sides kept ends this unstable chunk.
            var next = io;
            while (next < o.Length && (ma[next] < 0 || mb[next] < 0))
            {
                next++;
            }

            var ea = next < o.Length ? ma[next] : a.Length;
            var eb = next < o.Length ? mb[next] : b.Length;
            var co = o[io..next];
            var ca = a[ia..ea];
            var cb = b[ib..eb];
            if (co.SequenceEqual(ca))
            {
                output.AddRange(cb);
            }
            else if (co.SequenceEqual(cb) || ca.SequenceEqual(cb))
            {
                output.AddRange(ca);
            }
            else if (ko is not null && MergeByKey(co, ca, cb, ko[io..next], ka![ia..ea], kb![ib..eb], policy) is { } keyed)
            {
                output.AddRange(keyed.Lines);
                conflicted |= keyed.Conflicted;
            }
            else if (ko is not null && co.Length == 0 && DistinctKeyed(ka![ia..ea], kb![ib..eb]))
            {
                // Both added lines that each carry their own identity (say, time logged on two devices): both stay.
                output.AddRange(ca);
                output.AddRange(cb);
            }
            else
            {
                conflicted = true;
                output.AddRange(policy switch
                {
                    ConflictPolicy.First => ca,
                    ConflictPolicy.Second => cb,
                    _ => [.. ca, .. cb.Where(line => !ca.Contains(line))],
                });
            }

            io = next;
            ia = ea;
            ib = eb;
        }

        return new TextMergeResult(Join(output), conflicted);
    }

    /// <summary>
    /// Lines that carry their own identity (a subtask's block id, a frontmatter key) are merged one by one when the
    /// clashing stretch has the same keys in the same order on all three sides: checking off one step while renaming
    /// the next is not a conflict.
    /// </summary>
    private static (List<string> Lines, bool Conflicted)? MergeByKey(string[] o, string[] a, string[] b, string?[] ko, string?[] ka, string?[] kb, ConflictPolicy policy)
    {
        if (o.Length != a.Length || o.Length != b.Length || o.Length == 0)
        {
            return null;
        }

        var lines = new List<string>();
        var conflicted = false;
        for (var i = 0; i < o.Length; i++)
        {
            var key = ko[i];
            if (key is null || ka[i] != key || kb[i] != key)
            {
                return null;
            }

            if (a[i] == o[i] || a[i] == b[i])
            {
                lines.Add(b[i] == o[i] ? a[i] : b[i]);
            }
            else if (b[i] == o[i])
            {
                lines.Add(a[i]);
            }
            else
            {
                // The same step changed on both sides: one version (a step can't be two lines); the other stays
                // available with the conflict, to choose instead.
                conflicted = true;
                lines.Add(policy == ConflictPolicy.Second ? b[i] : a[i]);
            }
        }

        return (lines, conflicted);
    }

    private static bool DistinctKeyed(string?[] a, string?[] b) =>
        a.Length > 0 && b.Length > 0 && a.All(k => k is not null) && b.All(k => k is not null) && !a.Intersect(b).Any();

    /// <summary>Lines with their line endings (the last may have none).</summary>
    private static string[] Split(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return [.. lines];
    }

    private static string Join(List<string> lines)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            // A last line without its newline that ends up in the middle gets one.
            if (sb.Length > 0 && sb[^1] != '\n')
            {
                sb.Append('\n');
            }

            sb.Append(line);
        }

        return sb.ToString();
    }

    /// <summary>For each base line, the index of the line it matches in <paramref name="other"/> (longest common subsequence), or -1.</summary>
    private static int[] Match(string[] o, string[] other)
    {
        var map = Enumerable.Repeat(-1, o.Length).ToArray();
        var prefix = 0;
        while (prefix < o.Length && prefix < other.Length && o[prefix] == other[prefix])
        {
            map[prefix] = prefix;
            prefix++;
        }

        var suffix = 0;
        while (suffix < o.Length - prefix && suffix < other.Length - prefix && o[o.Length - 1 - suffix] == other[other.Length - 1 - suffix])
        {
            map[o.Length - 1 - suffix] = other.Length - 1 - suffix;
            suffix++;
        }

        var n = o.Length - prefix - suffix;
        var m = other.Length - prefix - suffix;
        if (n == 0 || m == 0)
        {
            return map;
        }

        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = o[prefix + i] == other[prefix + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        for (int i = 0, j = 0; i < n && j < m;)
        {
            if (o[prefix + i] == other[prefix + j])
            {
                map[prefix + i] = prefix + j;
                i++;
                j++;
            }
            else if (lcs[i + 1, j] >= lcs[i, j + 1])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return map;
    }
}
