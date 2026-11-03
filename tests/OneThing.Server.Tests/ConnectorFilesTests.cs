using OneThing.Core.Connectors;
using OneThing.Server.Plugins.Connectors;

namespace OneThing.Server.Tests;

/// <summary>A connector's files on this computer: the sync loop and Settings read and save them at the same time.</summary>
public sealed class ConnectorFilesTests : IDisposable
{
    private readonly string _data = Directory.CreateTempSubdirectory("onething-connector-files-").FullName;

    public void Dispose() => Directory.Delete(_data, recursive: true);

    [Fact]
    public async Task Saving_and_reading_the_links_at_the_same_time_never_fails()
    {
        var files = new ConnectorFiles(_data, "github-test");
        files.SaveLinks([new ConnectorLink(Guid.NewGuid(), "1", null)]);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        var writers = Enumerable.Range(0, 3).Select(w => Task.Run(() =>
        {
            for (var i = 0; DateTime.UtcNow < deadline; i++)
            {
                files.SaveLinks([new ConnectorLink(Guid.NewGuid(), $"{w}-{i}", null)]);
            }
        }));
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (DateTime.UtcNow < deadline)
            {
                Assert.Single(files.Links);
            }
        }));

        await Task.WhenAll(writers.Concat(readers));

        Assert.Single(files.Links);
        Assert.Empty(Directory.GetFiles(files.Folder, "*.tmp"));
    }
}
