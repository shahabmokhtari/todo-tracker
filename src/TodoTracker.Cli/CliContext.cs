namespace TodoTracker.Cli;

/// <summary>Where <c>tt</c> reads and writes, and the clock it uses (tests swap these).</summary>
public sealed class CliContext
{
    public required TextWriter Out { get; init; }

    public required TextWriter Error { get; init; }

    public TextReader In { get; init; } = TextReader.Null;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>App data folder (settings, chosen vault). Null: the app's default (<c>TODOTRACKER_DATA</c> or local app data).</summary>
    public string? DataDirectory { get; init; }

    /// <summary>Tasks folder. Null: the one the app uses.</summary>
    public string? VaultPath { get; init; }

    /// <summary>Save a version after each change (needs git).</summary>
    public bool History { get; init; } = true;

    public string WorkingDirectory { get; init; } = Environment.CurrentDirectory;

    /// <summary>Per-vault locks and version history (null: local app data).</summary>
    public string? LockDirectory { get; init; }

    /// <summary>The git used for version history.</summary>
    public string Git { get; init; } = "git";

    /// <summary>Who changes are attributed to when <c>--as</c> isn't given (<c>TT_AGENT</c>).</summary>
    public string? Agent { get; init; } = Environment.GetEnvironmentVariable("TT_AGENT");

    public static CliContext ForConsole() => new()
    {
        Out = Console.Out,
        Error = Console.Error,
        In = Console.In,
    };
}
