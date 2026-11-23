using System.Text.Json.Nodes;

namespace OneThing.Server.Tests;

/// <summary>The Mac app ships onething (and ot, a link to it) in Contents/Resources/Server/cli, next to the server.</summary>
public sealed class MacCliTests
{
    [Fact]
    public void The_command_line_beside_the_server_is_what_connect_an_ai_app_shows_in_full()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The Mac's layout (a link for ot); Windows has its own test.
        }

        var server = Directory.CreateTempSubdirectory("onething-mac-server-").FullName;
        try
        {
            var cli = Directory.CreateDirectory(Path.Combine(server, "cli")).FullName;
            File.WriteAllText(Path.Combine(cli, "onething"), string.Empty);
            File.CreateSymbolicLink(Path.Combine(cli, "ot"), "onething");

            var found = AiSetups.FindCli(server);
            var setups = AiSetups.For(new ConnectionDto("http://127.0.0.1:5317", "http://127.0.0.1:5317/mcp", "t", new { }), found);

            Assert.Equal(Path.Combine(cli, "onething"), found);
            var desktop = JsonNode.Parse(setups.Setups.Single(s => s.Id == "claude-desktop").Snippet)!;
            Assert.Equal(found, desktop["mcpServers"]!["onething"]!["command"]!.GetValue<string>());
            Assert.Contains($"-- {found} mcp", setups.Setups.Single(s => s.Id == "claude-code").Snippet, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(server, recursive: true);
        }
    }
}
