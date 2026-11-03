using System.Globalization;
using OneThing.Server;

// Standalone host for the web dashboard, REST API, and MCP endpoint (the Windows sidebar embeds the same server).
//   dotnet run --project src/OneThing.Web -- [--data <dir>] [--port <port>]
var options = new OneThingServerOptions
{
    ApiToken = Environment.GetEnvironmentVariable("ONETHING_TOKEN"),
};
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data" when i + 1 < args.Length:
            options.DataDirectory = args[++i];
            break;
        case "--port" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            {
                Console.Error.WriteLine($"The port must be a number from 1 to 65535, not \"{args[i]}\".");
                return 1;
            }

            options.Port = port;
            break;
        case "--import-legacy":
            // Started by the Mac app: the tasks it kept on its own join the tasks folder, even one that has tasks.
            options.ImportLegacyIntoExisting = true;
            break;
        case "--no-history":
            // No version history (it needs git; on a Mac without the developer tools, git asks to install them).
            options.EnableHistory = false;
            break;
    }
}

var app = OneThingHost.Build(OneThingHost.CreateBuilder(options));
var connection = OneThingHost.GetConnection(app.Services);
if (ParentProcess.FromArguments(args) is { } parent)
{
    // Started by an app (the Mac app): stop when it's gone.
    _ = ParentProcess.WatchAsync(parent, app.Lifetime.StopApplication, TimeSpan.FromSeconds(1), app.Lifetime.ApplicationStopping);
}

Console.WriteLine($"One Thing is running. Open within 15 minutes: {OneThingHost.CreateLaunchUrl(app.Services, "/", TimeSpan.FromMinutes(15))}");
Console.WriteLine("Later visits: paste the API token on the sign-in page.");
Console.WriteLine($"MCP endpoint: {connection.McpUrl} (Bearer token stored in {Path.Combine(options.DataDirectory, "api-token")})");
await app.RunAsync();
return 0;
