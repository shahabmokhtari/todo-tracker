namespace OneThing.Server.Tests;

/// <summary>settings.json: saved in one step, and a file that can't be read never stops the app (or ot) from starting.</summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _data = Directory.CreateTempSubdirectory("onething-settings-").FullName;

    private string File(string name) => Path.Combine(_data, name);

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    [Fact]
    public void A_settings_file_that_cant_be_read_is_set_aside_and_the_defaults_are_used()
    {
        System.IO.File.WriteAllText(File("settings.json"), "{\"theme\":\"da");

        var settings = new SettingsStore(new OneThingServerOptions { DataDirectory = _data });

        Assert.Equal("system", settings.ToDto().Theme);
        Assert.Contains("settings.json.bad", settings.Problem, StringComparison.Ordinal);
        Assert.Equal("{\"theme\":\"da", System.IO.File.ReadAllText(File("settings.json.bad")));

        settings.SetTheme("dark");
        Assert.Equal("dark", new SettingsStore(new OneThingServerOptions { DataDirectory = _data }).Current.Theme);
        Assert.Empty(Directory.GetFiles(_data, "*.tmp"));
    }

    [Fact]
    public async Task The_app_starts_with_a_settings_file_that_cant_be_read()
    {
        await using var server = await ServerFixture.StartAsync(o =>
        {
            o.DataDirectory = _data;
            System.IO.File.WriteAllText(File("settings.json"), "\0\0\0");
        });

        Assert.Equal("system", (await server.Client().GetJson("/api/settings"))["theme"]!.GetValue<string>());
    }

    [Fact]
    public void Secret_files_are_readable_only_by_you()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps these in your profile, closed to other users.");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Made by an older version with the usual mode: tightened when the app next starts.
        System.IO.File.WriteAllText(File("owner-key"), new string('a', 64));
        System.IO.File.SetUnixFileMode(File("owner-key"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        ApiToken.LoadOrCreate(new OneThingServerOptions { DataDirectory = _data });
        OwnerKey.LoadOrCreate(new OneThingServerOptions { DataDirectory = _data });
        new SettingsStore(new OneThingServerOptions { DataDirectory = _data }).SetTheme("dark");

        foreach (var name in new[] { "api-token", "owner-key", "settings.json" })
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, System.IO.File.GetUnixFileMode(File(name)));
        }
    }
}
