namespace TodoTracker.Core.Tests.Vault;

using TodoTracker.Core.Vault;

public sealed class ObsidianVaultsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tt-obsidian-" + Guid.NewGuid().ToString("N"));

    public ObsidianVaultsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Vaults_registered_in_obsidian_are_found_sorted_by_name_and_missing_ones_skipped()
    {
        var work = Directory.CreateDirectory(Path.Combine(_dir, "Work notes")).FullName;
        var home = Directory.CreateDirectory(Path.Combine(_dir, "home")).FullName;
        var config = Path.Combine(_dir, "obsidian.json");
        File.WriteAllText(config, System.Text.Json.JsonSerializer.Serialize(new
        {
            vaults = new Dictionary<string, object>
            {
                ["a1"] = new { path = work, ts = 1 },
                ["b2"] = new { path = home + Path.DirectorySeparatorChar, open = true },
                ["c3"] = new { path = Path.Combine(_dir, "deleted") },
                ["d4"] = new { ts = 2 },
            },
        }));

        var vaults = ObsidianVaults.Discover(config);

        Assert.Equal(["home", "Work notes"], vaults.Select(v => v.Name));
        Assert.Equal(Path.Combine(work, "Todo Tracker"), ObsidianVaults.TaskFolderIn(vaults[1]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("{\"vaults\": []}")]
    [InlineData("{}")]
    public void A_missing_or_unexpected_config_means_no_vaults(string? text)
    {
        var config = Path.Combine(_dir, "obsidian.json");
        if (text is not null)
        {
            File.WriteAllText(config, text);
        }

        Assert.Empty(ObsidianVaults.Discover(config));
    }

    [Fact]
    public void Without_obsidian_its_links_go_to_its_download_page_not_a_store_search()
    {
        // Windows answers an obsidian:// link it can't open with "look for an app in the Microsoft Store", where
        // Obsidian isn't.
        var config = Path.Combine(_dir, "obsidian.json");
        Assert.False(ObsidianVaults.IsInstalled(config));
        Assert.Equal("https://obsidian.md/download", ObsidianVaults.LinkOrDownload("obsidian://open?path=x", config));

        File.WriteAllText(config, "{\"vaults\":{}}");
        Assert.True(ObsidianVaults.IsInstalled(config));
        Assert.Equal("obsidian://open?path=x", ObsidianVaults.LinkOrDownload("obsidian://open?path=x", config));
    }

    [Fact]
    public void Open_links_escape_the_path()
    {
        Assert.Equal("obsidian://open?path=C%3A%5CNotes%5CA%20%26%20B.md", ObsidianVaults.OpenUrl(@"C:\Notes\A & B.md"));
    }
}
