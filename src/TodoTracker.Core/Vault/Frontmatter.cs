using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace TodoTracker.Core.Vault;

/// <summary>
/// YAML frontmatter: values are read with a real YAML parser, while each top-level key's original text is kept so
/// keys the app doesn't own (aliases, cssclasses, plugin properties…) are written back exactly as they were.
/// </summary>
internal sealed class Frontmatter
{
    private Frontmatter(YamlMappingNode values, IReadOnlyList<(string? Key, string Raw)> blocks)
    {
        Values = values;
        Blocks = blocks;
    }

    public static Frontmatter Empty { get; } = new(new YamlMappingNode(), []);

    public YamlMappingNode Values { get; }

    /// <summary>Top-level entries in file order: key (null for leading comments) and raw text (LF, ends with \n).</summary>
    public IReadOnlyList<(string? Key, string Raw)> Blocks { get; }

    /// <summary>Splits <c>---</c> frontmatter off <paramref name="text"/> (LF-normalized). Returns the body.</summary>
    public static (Frontmatter Frontmatter, string Body, bool Present) Split(string text)
    {
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (Empty, text, false);
        }

        var end = -1;
        var position = 4;
        while (position <= text.Length)
        {
            var next = text.IndexOf('\n', position);
            var line = next < 0 ? text[position..] : text[position..next];
            if (line.TrimEnd() is "---" or "...")
            {
                end = position;
                position = next < 0 ? text.Length : next + 1;
                break;
            }

            if (next < 0)
            {
                break;
            }

            position = next + 1;
        }

        if (end < 0)
        {
            // An opening --- without a closing one is just a horizontal rule in the body.
            return (Empty, text, false);
        }

        var yaml = text[4..end];
        return (Parse(yaml), text[position..], true);
    }

    public static Frontmatter Parse(string yaml)
    {
        YamlMappingNode values;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            values = stream.Documents.Count == 0 || stream.Documents[0].RootNode is YamlScalarNode { Value: null or "" }
                ? new YamlMappingNode()
                : stream.Documents[0].RootNode as YamlMappingNode ?? throw new VaultFormatException("The frontmatter must be a list of `key: value` properties.");
        }
        catch (YamlException ex)
        {
            throw new VaultFormatException($"The frontmatter isn't valid YAML (line {ex.Start.Line}): {ex.Message}", ex);
        }

        var blocks = new List<(string? Key, string Raw)>();
        string? key = null;
        var raw = new System.Text.StringBuilder();
        foreach (var line in yaml.Split('\n'))
        {
            if (line.Length == 0 && raw.Length == 0 && blocks.Count == 0 && key is null)
            {
                continue;
            }

            if (TopLevelKey(line) is { } k)
            {
                if (raw.Length > 0 || key is not null)
                {
                    blocks.Add((key, raw.ToString()));
                }

                key = k;
                raw.Clear();
            }

            raw.Append(line).Append('\n');
        }

        if (raw.Length > 0 || key is not null)
        {
            var last = raw.ToString();
            // yaml text ends with "\n" before the closing ---, which Split('\n') turns into a trailing empty line.
            blocks.Add((key, last.EndsWith("\n\n", StringComparison.Ordinal) ? last[..^1] : last));
        }

        return new Frontmatter(values, blocks);
    }

    public string? Scalar(string key) => Node(key) is YamlScalarNode s ? s.Value : null;

    public IReadOnlyList<string> List(string key) => Node(key) switch
    {
        YamlSequenceNode seq => seq.Children.OfType<YamlScalarNode>().Select(s => s.Value ?? string.Empty).Where(v => v.Length > 0).ToList(),
        YamlScalarNode { Value: { Length: > 0 } v } => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        _ => [],
    };

    public IReadOnlyList<YamlMappingNode> Maps(string key) => Node(key) is YamlSequenceNode seq ? seq.Children.OfType<YamlMappingNode>().ToList() : [];

    public bool Has(string key) => Node(key) is not null;

    public static string? Get(YamlMappingNode map, string key) =>
        map.Children.FirstOrDefault(p => p.Key is YamlScalarNode k && string.Equals(k.Value, key, StringComparison.OrdinalIgnoreCase)).Value is YamlScalarNode s ? s.Value : null;

    private YamlNode? Node(string key) =>
        Values.Children.FirstOrDefault(p => p.Key is YamlScalarNode k && string.Equals(k.Value, key, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? TopLevelKey(string line)
    {
        if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] is '#' or '-')
        {
            return null;
        }

        var colon = line.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && (colon == line.Length - 1 || char.IsWhiteSpace(line[colon + 1])) ? line[..colon].Trim().Trim('"', '\'') : null;
    }
}
