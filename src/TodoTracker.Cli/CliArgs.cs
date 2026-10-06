namespace TodoTracker.Cli;

/// <summary>A mistake in how <c>tt</c> was called (exit code 2).</summary>
public sealed class CliUsageException(string message) : Exception(message);

/// <summary>
/// Parsed command line: <c>tt [command] [words…] [--option value]… [--flag]…</c>. Only <c>--long</c> options and a few
/// known short ones are options, so words such as <c>-tech</c> (remove a tag) and <c>-</c> (read stdin) stay words.
/// </summary>
internal sealed class CliArgs
{
    public static readonly HashSet<string> FlagNames = ["json", "all", "top", "help", "version", "no-history"];

    public static readonly HashSet<string> ValueNames =
        ["vault", "data", "as", "group", "under", "details", "tag", "label", "due", "snooze", "delay", "title", "priority", "index"];

    public static readonly HashSet<string> GlobalNames = ["vault", "data", "as", "json", "help", "no-history"];

    private static readonly Dictionary<string, string> ShortNames = new(StringComparer.Ordinal)
    {
        ["-g"] = "group",
        ["-h"] = "help",
        ["-?"] = "help",
        ["-j"] = "json",
        ["-v"] = "version",
    };

    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    private CliArgs(string command, List<string> words)
    {
        Command = command;
        Words = words;
    }

    public string Command { get; }

    public IReadOnlyList<string> Words { get; }

    public bool Json => Has("json");

    public bool Help => Has("help");

    public static CliArgs Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var words = new List<string>();
        var values = new List<(string Name, string Value)>();
        var flags = new List<string>();
        var onlyWords = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (onlyWords || arg.Length < 2 || arg[0] != '-')
            {
                words.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                onlyWords = true;
                continue;
            }

            string name;
            string? inline = null;
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = arg.IndexOf('=', StringComparison.Ordinal);
                name = eq > 0 ? arg[2..eq] : arg[2..];
                inline = eq > 0 ? arg[(eq + 1)..] : null;
            }
            else if (ShortNames.TryGetValue(arg, out var full))
            {
                name = full;
            }
            else
            {
                words.Add(arg);
                continue;
            }

            if (FlagNames.Contains(name))
            {
                if (inline is not null)
                {
                    throw new CliUsageException($"--{name} doesn't take a value.");
                }

                flags.Add(name);
            }
            else if (ValueNames.Contains(name))
            {
                var value = inline ?? (i + 1 < args.Count ? args[++i] : throw new CliUsageException($"--{name} needs a value."));
                values.Add((name, value));
            }
            else
            {
                throw new CliUsageException($"Unknown option {arg}. See tt help.");
            }
        }

        var command = words.Count > 0 ? words[0].ToLowerInvariant() : "now";
        var parsed = new CliArgs(command, words.Count > 0 ? words.Skip(1).ToList() : []);
        foreach (var (name, value) in values)
        {
            if (!parsed._values.TryGetValue(name, out var list))
            {
                parsed._values[name] = list = [];
            }

            list.Add(value);
        }

        parsed._flags.UnionWith(flags);
        return parsed;
    }

    public bool Has(string name) => _flags.Contains(name) || _values.ContainsKey(name);

    public string? Value(string name) => _values.TryGetValue(name, out var list) ? list[^1] : null;

    public IReadOnlyList<string> Values(string name) => _values.TryGetValue(name, out var list) ? list : [];

    /// <summary>Fails when an option that this command doesn't understand was given.</summary>
    public void Allow(params string[] names)
    {
        foreach (var name in _flags.Concat(_values.Keys))
        {
            if (!GlobalNames.Contains(name) && !names.Contains(name, StringComparer.Ordinal) && name != "version")
            {
                throw new CliUsageException($"tt {Command} doesn't take --{name}. See tt help {Command}.");
            }
        }
    }

    public string WordsFrom(int index, string what)
    {
        var text = string.Join(' ', Words.Skip(index)).Trim();
        return text.Length > 0 ? text : throw new CliUsageException($"Say {what}. See tt help {Command}.");
    }

    public string Word(int index, string what) =>
        index < Words.Count && Words[index].Trim().Length > 0 ? Words[index] : throw new CliUsageException($"Say {what}. See tt help {Command}.");
}
