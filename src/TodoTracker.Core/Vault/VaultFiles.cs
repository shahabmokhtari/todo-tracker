using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TodoTracker.Core.Vault;

/// <summary>Where the vault lives and how it behaves.</summary>
/// <param name="Root">Vault folder (e.g. <c>Documents/Todo Tracker</c> or a folder inside an Obsidian vault).</param>
public sealed record VaultOptions(string Root)
{
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Local (never synced) folder for the cross-process write lock. Default: local app data.</summary>
    public string? LockDirectory { get; init; }

    /// <summary>
    /// Where version history is kept (default: local app data, or next to <see cref="LockDirectory"/> when that is set).
    /// Never inside the vault: two devices running git on one synced repository would corrupt it.
    /// </summary>
    public string? HistoryDirectory { get; init; }

    /// <summary>A <c>board.json</c> from earlier versions to import into an empty vault (then renamed to <c>.migrated</c>).</summary>
    public string? LegacyBoardPath { get; init; }

    /// <summary>Watch the folder for edits made outside the app (Obsidian, editors, agents, sync clients).</summary>
    public bool Watch { get; init; } = true;

    public long MaxAttachmentBytes { get; init; } = 25L * 1024 * 1024;

    /// <summary>How long a file must be left alone before the app reacts to an edit in it (e.g. unlocks the next step
    /// after a box was checked), so it never writes to a file someone is still typing in.</summary>
    public TimeSpan EditSettleTime { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Test hook: called with the vault-relative path before each task file is written.</summary>
    internal Action<string>? BeforeWrite { get; init; }

    public int MaxRichBytes { get; init; } = 2 * 1024 * 1024;
}

/// <summary>A file the app couldn't read; it is shown to the user and never overwritten until fixed.</summary>
public sealed record VaultProblem(string Path, string Message);

/// <summary>File-name and lock helpers for the vault.</summary>
internal static partial class VaultFiles
{
    public const string MetaFolder = ".todo-tracker";
    public const string AttachmentsFolder = "_attachments";

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A file or folder name that works on Windows, macOS, Linux, and in Obsidian links.</summary>
    public static string SafeName(string title, int maxLength = 80)
    {
        var cleaned = Unsafe().Replace(title.Normalize(NormalizationForm.FormC), " ");
        cleaned = Spaces().Replace(cleaned, " ").Trim().TrimEnd('.', ' ').TrimStart('.', ' ');
        if (cleaned.Length > maxLength)
        {
            cleaned = cleaned[..maxLength].TrimEnd('.', ' ');
        }

        if (cleaned.Length == 0)
        {
            cleaned = "Untitled";
        }

        return Reserved.Contains(cleaned) ? cleaned + "_" : cleaned;
    }

    /// <summary>A group folder name: like <see cref="SafeName"/>, without a leading <c>_</c> or <c>.</c> (those folders are skipped).</summary>
    public static string SafeFolderName(string name)
    {
        var cleaned = SafeName(name, 60).TrimStart('_', '.', ' ');
        return cleaned.Length == 0 ? "Group" : cleaned;
    }

    /// <summary><paramref name="stem"/>, or "stem 2", "stem 3"… so it is unique (case-insensitively) in the folder.</summary>
    public static string UniqueStem(string stem, Func<string, bool> taken)
    {
        if (!taken(stem))
        {
            return stem;
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} {n}";
            if (!taken(candidate))
            {
                return candidate;
            }
        }
    }

    public static string DefaultLockDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "TodoTracker", "locks");

    public static string LockPath(string lockDirectory, string root)
    {
        var key = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!OperatingSystem.IsLinux())
        {
            key = key.ToUpperInvariant();
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(lockDirectory, $"vault-{hash}.lock");
    }

    /// <summary>Async version of <see cref="AcquireLock"/> (doesn't block a thread while another process holds the lock).</summary>
    public static async Task<FileStream> AcquireLockAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new StoreLockedException("Another Todo Tracker process kept the vault busy for too long. Try again.", ex);
            }
        }
    }

    /// <summary>Holds the exclusive per-vault lock (same machine). Waits up to <paramref name="timeout"/>.</summary>
    public static FileStream AcquireLock(string path, TimeSpan timeout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
            catch (IOException ex)
            {
                throw new StoreLockedException("Another Todo Tracker process kept the vault busy for too long. Try again.", ex);
            }
        }
    }

    /// <summary>Writes atomically (temp file + replace) so readers and sync clients never see half a file.</summary>
    public static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public static string Relative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    public static string Full(string root, string relative) => Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

    [GeneratedRegex(@"[<>:""/\\|?*#^\[\]\x00-\x1F]")]
    private static partial Regex Unsafe();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
