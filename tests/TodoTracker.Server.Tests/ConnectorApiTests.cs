using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using TodoTracker.Core;
using TodoTracker.Core.Tests.Connectors;
using TodoTracker.Server.Plugins.Connectors;

namespace TodoTracker.Server.Tests;

public sealed class ConnectorApiTests
{
    private const string Base = "/api/plugins/connectors";

    private static Task<ServerFixture> StartAsync(string plugins, Action<IServiceCollection> services) =>
        ServerFixture.StartAsync(
            o =>
            {
                Directory.CreateDirectory(o.DataDirectory);
                File.WriteAllText(Path.Combine(o.DataDirectory, "plugins.json"), plugins);
            },
            services);

    private static async Task<Guid> GroupAsync(ServerFixture server, string name) =>
        await server.Store.UpdateAsync(b => b.Groups.FirstOrDefault(g => g.Name == name)?.Id ?? b.AddGroup(name, null, Actor.User, ServerFixture.T0).Id);

    [Fact]
    public async Task Connectors_are_off_until_switched_on()
    {
        await using var server = await ServerFixture.StartAsync();

        Assert.False((await server.Client().GetAsync(Base)).IsSuccessStatusCode);
        Assert.False((await server.Client().PostAsync($"{Base}/notion/sync", null)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Notion_token_database_preview_and_sync()
    {
        using var notion = new FakeNotion();
        await using var server = await StartAsync("""{"connector-notion": true}""", s => s.AddHttpClient(NotionConnector.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => notion));
        var client = server.Client();
        var group = await GroupAsync(server, "Synced");
        await server.Store.UpdateAsync(b => b.AddTask(new NewTask("Write report") { GroupId = group }, Actor.User, ServerFixture.T0));
        notion.AddPage("p1", new JsonObject { ["Task"] = new JsonObject { ["type"] = "title", ["title"] = new JsonArray(new JsonObject { ["plain_text"] = "Call the bank" }) } });

        var start = (await client.GetJson(Base)).AsArray().Single()!;
        Assert.Equal("Paste your Notion integration's token.", start["missing"]!.GetValue<string>());

        var bad = await client.PutAsJsonAsync($"{Base}/notion/token", new { token = "secret_wrong_one" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("API token is invalid", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var databases = await (await client.PutAsJsonAsync($"{Base}/notion/token", new { token = "secret_token" })).Json();
        Assert.Equal("Tasks", databases[0]!["title"]!.GetValue<string>());
        // The token is kept on this computer only, never as plain text on Windows.
        var secret = await File.ReadAllBytesAsync(Path.Combine(server.DataDirectory, "connectors", "notion", "secret"));
        Assert.Equal(!OperatingSystem.IsWindows(), System.Text.Encoding.UTF8.GetString(secret).Contains("secret_token", StringComparison.Ordinal));

        await (await client.PutAsJsonAsync($"{Base}/notion", new { target = FakeNotion.Db, targetName = "Tasks", groupId = group })).Json();
        var preview = await client.PostJson($"{Base}/notion/preview");
        Assert.Equal((1, 1), (preview["addHere"]!.GetValue<int>(), preview["sendThere"]!.GetValue<int>()));
        Assert.Single(notion.Pages); // a preview changes nothing

        var synced = await client.PostJson($"{Base}/notion/sync");

        Assert.Equal("1 added here, 1 sent", synced["lastResult"]!.GetValue<string>());
        Assert.True(synced["enabled"]!.GetValue<bool>());
        Assert.Equal(2, synced["linked"]!.GetValue<int>());
        Assert.Equal(2, notion.Pages.Count);
        Assert.NotNull(await server.Store.ReadAsync(b => b.Items.FirstOrDefault(i => i.Title == "Call the bank" && i.GroupId == group)));

        var gone = await (await client.DeleteAsync($"{Base}/notion")).Json();
        Assert.Equal("Paste your Notion integration's token.", gone["missing"]!.GetValue<string>());
        Assert.Equal(0, gone["linked"]!.GetValue<int>());
    }

    [Fact]
    public async Task Microsoft_To_Do_client_id_sign_in_list_and_sync()
    {
        using var graph = new FakeGraph();
        var signIn = new FakeSignIn();
        graph.Add("t1", "From To Do", importance: "high");
        await using var server = await StartAsync("""{"connector-mstodo": true}""", s =>
        {
            s.AddHttpClient(MicrosoftToDoConnector.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => graph);
            s.AddSingleton<Func<ConnectorFiles, IMicrosoftSignIn>>(_ => _ => signIn);
        });
        var client = server.Client();
        var group = await GroupAsync(server, "Synced");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{Base}/mstodo", new { clientId = "not a guid" })).StatusCode);
        var configured = await (await client.PutAsJsonAsync($"{Base}/mstodo", new { clientId = "11111111-2222-3333-4444-555555555555" })).Json();
        Assert.Equal("Sign in to Microsoft.", configured["missing"]!.GetValue<string>());

        var code = await client.PostJson($"{Base}/mstodo/signin");
        Assert.Equal("ABCD-1234", code["userCode"]!.GetValue<string>());
        signIn.Finish();
        Assert.Equal("signedIn", (await client.GetJson($"{Base}/mstodo/signin"))["state"]!.GetValue<string>());

        var lists = await client.GetJson($"{Base}/mstodo/lists");
        Assert.Equal("Tasks", lists[0]!["name"]!.GetValue<string>());
        await (await client.PutAsJsonAsync($"{Base}/mstodo", new { target = FakeGraph.List, targetName = "Tasks", groupId = group, direction = "importOnly" })).Json();

        var synced = await client.PostJson($"{Base}/mstodo/sync");

        Assert.Equal("1 added here", synced["lastResult"]!.GetValue<string>());
        var imported = await server.Store.ReadAsync(b => b.Items.Single(i => i.Title == "From To Do"));
        Assert.Equal((Priority.High, group), (imported.Priority, imported.GroupId));
        Assert.Equal(ActorKind.Connector, await server.Store.ReadAsync(b => b.Activity.First(a => a.ItemId == imported.Id).Actor.Kind));
    }

    private sealed class FakeSignIn : IMicrosoftSignIn
    {
        public MicrosoftSignInView View { get; private set; } = new("signedOut");

        public bool SignedIn => View.State == "signedIn";

        public Task<MicrosoftSignInView> StartAsync(string clientId, CancellationToken cancellationToken)
        {
            View = new MicrosoftSignInView("waiting", UserCode: "ABCD-1234", VerificationUri: "https://microsoft.com/devicelogin");
            return Task.FromResult(View);
        }

        public void Finish() => View = new MicrosoftSignInView("signedIn", "me@example.com");

        public Task<string> TokenAsync(CancellationToken cancellationToken) => Task.FromResult("graph-token");

        public void SignOut() => View = new MicrosoftSignInView("signedOut");
    }
}
