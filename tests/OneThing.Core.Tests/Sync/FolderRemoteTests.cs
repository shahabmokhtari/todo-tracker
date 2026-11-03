using OneThing.Core.Sync;
using Xunit;

namespace OneThing.Core.Tests.Sync;

/// <summary>The shared folder (OneDrive, iCloud Drive): what publishing reads and writes there.</summary>
public sealed class FolderRemoteTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Content = "Plan the trip"u8.ToArray();
    private readonly string _root = Directory.CreateTempSubdirectory("tt-folder-remote-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static DeviceSnapshot Snapshot() =>
        new("laptop00000000000000000000000000", "Laptop", T0, [new SyncEntry("f/trip.md", "trip.md", SyncHash.Of(Content))]);

    private string BlobPath() => Path.Combine(_root, "blobs", SyncHash.Of(Content)[..2], SyncHash.Of(Content));

    [Fact]
    public async Task A_stored_file_that_looks_the_same_as_last_time_is_not_read_again()
    {
        // Reading every stored file on each publish downloads all of them from OneDrive's Files On-Demand.
        var remote = new FolderRemote(_root);
        await remote.PublishAsync(Snapshot(), _ => Content, null, TestContext.Current.CancellationToken);
        var written = File.GetLastWriteTimeUtc(BlobPath());

        // Same length and time: if publishing read it, it would see the change and write it again.
        await File.WriteAllBytesAsync(BlobPath(), "Plan the TRIP"u8.ToArray(), TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(BlobPath(), written);
        var reads = 0;
        await remote.PublishAsync(Snapshot(), _ => { reads++; return Content; }, null, TestContext.Current.CancellationToken);

        Assert.Equal(0, reads);
        Assert.Equal("Plan the TRIP"u8.ToArray(), await File.ReadAllBytesAsync(BlobPath(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_stored_file_damaged_since_it_was_checked_is_written_again()
    {
        var remote = new FolderRemote(_root);
        await remote.PublishAsync(Snapshot(), _ => Content, null, TestContext.Current.CancellationToken);

        // A sync client's half download: shorter, and newer.
        await File.WriteAllBytesAsync(BlobPath(), "Plan"u8.ToArray(), TestContext.Current.CancellationToken);
        await remote.PublishAsync(Snapshot(), _ => Content, null, TestContext.Current.CancellationToken);

        Assert.Equal(Content, await File.ReadAllBytesAsync(BlobPath(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_stored_file_never_checked_by_this_device_is_checked_once()
    {
        // Another device wrote it damaged, before this one started.
        Directory.CreateDirectory(Path.GetDirectoryName(BlobPath())!);
        await File.WriteAllBytesAsync(BlobPath(), "Plan the TRIP"u8.ToArray(), TestContext.Current.CancellationToken);

        await new FolderRemote(_root).PublishAsync(Snapshot(), _ => Content, null, TestContext.Current.CancellationToken);

        Assert.Equal(Content, await File.ReadAllBytesAsync(BlobPath(), TestContext.Current.CancellationToken));
    }
}
