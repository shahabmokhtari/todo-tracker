using System.Globalization;
using TodoTracker.Server;

// Standalone host for the web dashboard, REST API, and MCP endpoint (the Windows sidebar embeds the same server).
//   dotnet run --project src/TodoTracker.Web -- [--data <dir>] [--port <port>]
var options = new TodoTrackerServerOptions
{
    ApiToken = Environment.GetEnvironmentVariable("TODOTRACKER_TOKEN"),
};
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data" when i + 1 < args.Length:
            options.DataDirectory = args[++i];
            break;
        case "--port" when i + 1 < args.Length:
            options.Port = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
    }
}

var app = TodoTrackerHost.Build(TodoTrackerHost.CreateBuilder(options));
var connection = TodoTrackerHost.GetConnection(app.Services);
Console.WriteLine($"Todo Tracker is running. Open within 15 minutes: {TodoTrackerHost.CreateLaunchUrl(app.Services, "/", TimeSpan.FromMinutes(15))}");
Console.WriteLine("Later visits: paste the API token on the sign-in page.");
Console.WriteLine($"MCP endpoint: {connection.McpUrl} (Bearer token stored in {Path.Combine(options.DataDirectory, "api-token")})");
await app.RunAsync();
