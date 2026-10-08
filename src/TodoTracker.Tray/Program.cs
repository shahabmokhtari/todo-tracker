using System.Net.Http.Headers;
using Avalonia;
using TodoTracker.Server;
using TodoTracker.Tray;

var parsed = TrayArgs.Parse(args);
if (parsed.SmokeTest)
{
    return await SmokeTestAsync(parsed).ConfigureAwait(false);
}

return AppBuilder.Configure(() => new TrayApp { Args = parsed })
    .UsePlatformDetect()
    .StartWithClassicDesktopLifetime(args);

// CI: starts the server (no UI: no display there), checks it answers with the token, then quits.
static async Task<int> SmokeTestAsync(TrayArgs args)
{
    var options = TrayHost.Options(args);
    await using var host = await TrayHost.StartAsync(options, new SmokeShell()).ConfigureAwait(false);
    if (host is null)
    {
        Console.Error.WriteLine("Another copy is running.");
        return 2;
    }

    using var http = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };
    var token = await File.ReadAllTextAsync(Path.Combine(options.DataDirectory, "api-token")).ConfigureAwait(false);
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
    using var response = await http.GetAsync(new Uri("/api/dashboard", UriKind.Relative)).ConfigureAwait(false);
    Console.WriteLine($"dashboard: {(int)response.StatusCode}; to do now: {host.ViewModel.NowCount}");
    return response.IsSuccessStatusCode ? 0 : 1;
}

internal sealed class SmokeShell : TodoTracker.Desktop.IDesktopShell
{
    public void OpenUrl(string url) => Console.WriteLine($"open {url}");

    public void CopyToClipboard(string text)
    {
    }

    public void RunOnUi(Action action) => action();

    public string? Prompt(string title, string message, string? initialValue = null) => null;

    public bool Confirm(string message) => false;
}
