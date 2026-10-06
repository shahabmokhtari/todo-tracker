namespace TodoTracker.Core.Vault;

/// <summary>What a folder holds, as Todo Tracker would see it (to warn before adopting a folder of existing notes).</summary>
/// <param name="Exists">Whether the folder exists.</param>
/// <param name="IsTaskFolder">Whether Todo Tracker already uses it (it has a <c>.todo-tracker</c> folder).</param>
/// <param name="Groups">Folders that would become groups (tabs).</param>
/// <param name="Tasks">Markdown notes in them that would become tasks.</param>
public sealed record VaultFolderSummary(bool Exists, bool IsTaskFolder, int Groups, int Tasks);

public static class VaultFolder
{
    private const int CountLimit = 100_000;

    public static VaultFolderSummary Inspect(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var root = Path.GetFullPath(folder);
        if (!Directory.Exists(root))
        {
            return new VaultFolderSummary(false, false, 0, 0);
        }

        var groups = 0;
        var tasks = 0;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.') || name.StartsWith('_'))
                {
                    continue;
                }

                groups++;
                tasks += Directory.EnumerateFiles(dir, "*.md", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System })
                    .Take(CountLimit - tasks)
                    .Count();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return new VaultFolderSummary(true, File.Exists(Path.Combine(root, VaultFiles.MetaFolder, "config.json")), groups, tasks);
    }
}
