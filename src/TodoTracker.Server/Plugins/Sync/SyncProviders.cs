using TodoTracker.Core.Sync;

namespace TodoTracker.Server.Plugins.Sync;

/// <summary>Whether a provider can be used here, and where it syncs (or why not).</summary>
public sealed record SyncAvailability(bool Available, string Detail);

/// <summary>
/// A place devices meet to sync (OneDrive, iCloud Drive, a GitHub gist, ...). Each is a plugin; the sync feature uses
/// the first available one in <see cref="Order"/> unless the person picked one.
/// </summary>
public interface ISyncProvider
{
    string Id { get; }

    string Name { get; }

    /// <summary>Lower comes first when choosing automatically.</summary>
    int Order { get; }

    /// <summary>Whether it's chosen automatically when available (a gist is only used when picked).</summary>
    bool Automatic { get; }

    SyncAvailability Check(string vaultRoot);

    ISyncRemote CreateRemote(string vaultRoot);
}

/// <summary>A cloud drive folder on this computer.</summary>
public sealed record CloudFolder(string Provider, string Account, string Root);

/// <summary>What the cloud drive providers look at (the real computer, or a test's).</summary>
public sealed record CloudEnvironment(Func<string, string?> Variable, string Home, Func<string, bool> DirectoryExists, Func<string, IEnumerable<string>> Directories, bool IsWindows, bool IsMac)
{
    /// <summary>This computer's cloud drives (none when TODOTRACKER_CLOUD=off, as tests set: they must never write to a real OneDrive).</summary>
    public static CloudEnvironment Current { get; } = new(
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        path => Environment.GetEnvironmentVariable("TODOTRACKER_CLOUD") != "off" && Directory.Exists(path),
        path => Environment.GetEnvironmentVariable("TODOTRACKER_CLOUD") != "off" && Directory.Exists(path) ? Directory.EnumerateDirectories(path) : [],
        OperatingSystem.IsWindows(),
        OperatingSystem.IsMacOS());

    /// <summary>OneDrive folders, work or school accounts first, then personal.</summary>
    public IReadOnlyList<CloudFolder> OneDrive()
    {
        var found = new List<CloudFolder>();
        void Add(string account, string? root)
        {
            if (root is { Length: > 0 } && DirectoryExists(root) && !found.Exists(f => SamePath(f.Root, root)))
            {
                found.Add(new CloudFolder("onedrive", account, root));
            }
        }

        if (IsWindows)
        {
            Add("work or school", Variable("OneDriveCommercial"));
            Add("personal", Variable("OneDriveConsumer"));
            Add("personal", Variable("OneDrive"));
        }
        else if (IsMac)
        {
            var dirs = Directories(Path.Combine(Home, "Library", "CloudStorage")).Where(d => Path.GetFileName(d).StartsWith("OneDrive", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
            foreach (var dir in dirs.Where(d => !Path.GetFileName(d).Equals("OneDrive-Personal", StringComparison.Ordinal)))
            {
                Add("work or school", dir);
            }

            foreach (var dir in dirs.Where(d => Path.GetFileName(d).Equals("OneDrive-Personal", StringComparison.Ordinal)))
            {
                Add("personal", dir);
            }
        }

        return found;
    }

    /// <summary>The iCloud Drive folder (macOS, or iCloud for Windows).</summary>
    public CloudFolder? ICloud()
    {
        var root = IsMac ? Path.Combine(Home, "Library", "Mobile Documents", "com~apple~CloudDocs")
            : IsWindows ? Path.Combine(Home, "iCloudDrive")
            : null;
        return root is not null && DirectoryExists(root) ? new CloudFolder("icloud", "iCloud", root) : null;
    }

    public bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), Comparison);

    public bool IsInside(string path, string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return full.StartsWith(parent, Comparison);
    }

    private StringComparison Comparison => IsWindows || IsMac ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

/// <summary>A cloud drive: devices meet in <c>Apps/TodoTrackerSync</c> inside it.</summary>
public abstract class CloudDriveProvider(CloudEnvironment environment) : ISyncProvider
{
    public const string Folder = "TodoTrackerSync";

    public abstract string Id { get; }

    public abstract string Name { get; }

    public abstract int Order { get; }

    public bool Automatic => true;

    protected CloudEnvironment Cloud { get; } = environment ?? throw new ArgumentNullException(nameof(environment));

    public SyncAvailability Check(string vaultRoot)
    {
        var folders = Folders();
        if (folders.Count == 0)
        {
            return new SyncAvailability(false, $"{Name} isn't set up on this computer.");
        }

        // A tasks folder that is already in this cloud drive is synced file by file by the drive itself; syncing it
        // here too would copy every change twice.
        if (folders.FirstOrDefault(f => Cloud.IsInside(vaultRoot, f.Root)) is { } holder)
        {
            return new SyncAvailability(false, $"Your tasks folder is already in {Name} ({holder.Root}), which copies it as it is. Move it out of {Name} to sync with merging instead.");
        }

        return new SyncAvailability(true, Path.Combine(folders[0].Root, "Apps", Folder));
    }

    public ISyncRemote CreateRemote(string vaultRoot)
    {
        var available = Check(vaultRoot);
        return available.Available ? new FolderRemote(available.Detail) : throw new InvalidOperationException(available.Detail);
    }

    protected abstract IReadOnlyList<CloudFolder> Folders();
}

/// <summary>OneDrive: a work or school account first, then a personal one.</summary>
public sealed class OneDriveSyncProvider(CloudEnvironment environment) : CloudDriveProvider(environment)
{
    public override string Id => "onedrive";

    public override string Name => "OneDrive";

    public override int Order => 10;

    protected override IReadOnlyList<CloudFolder> Folders() => Cloud.OneDrive();
}

/// <summary>iCloud Drive (on a Mac, or with iCloud for Windows).</summary>
public sealed class ICloudSyncProvider(CloudEnvironment environment) : CloudDriveProvider(environment)
{
    public override string Id => "icloud";

    public override string Name => "iCloud Drive";

    public override int Order => 20;

    protected override IReadOnlyList<CloudFolder> Folders() => Cloud.ICloud() is { } folder ? [folder] : [];
}
