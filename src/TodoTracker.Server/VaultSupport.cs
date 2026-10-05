using TodoTracker.Core;
using TodoTracker.Core.Vault;

namespace TodoTracker.Server;

/// <summary>One server per data folder (two would fight over the port, reminders, and settings).</summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _handle;

    private InstanceLock(FileStream handle)
    {
        _handle = handle;
    }

    public static InstanceLock Acquire(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "instance.lock");
        try
        {
            return new InstanceLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
        }
        catch (IOException ex)
        {
            throw new StoreLockedException($"Another Todo Tracker instance is already using {dataDirectory}.", ex);
        }
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>Where a task lives in the vault, for links in the API (file path, Obsidian link, rich version).</summary>
public sealed class VaultLinks(VaultBoardStore vault)
{
    public VaultBoardStore Vault => vault;

    public string? FileOf(Guid id) => vault.PathOf(id);

    public string? FullPathOf(Guid id) => FileOf(id) is { } rel ? Path.GetFullPath(Path.Combine(vault.RootPath, rel)) : null;

    public string? ObsidianUrlOf(Guid id) => FullPathOf(id) is { } full ? ObsidianVaults.OpenUrl(full) : null;

    public bool HasRich(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Parent is null && FullPathOf(item.Id) is { } full && File.Exists(Path.ChangeExtension(full, ".html"));
    }

    public static string AttachmentUrl(Guid itemId, Guid attachmentId) => $"/api/items/{itemId}/attachments/{attachmentId}";
}
