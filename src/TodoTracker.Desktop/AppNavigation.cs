namespace TodoTracker.Desktop;

/// <summary>Where a link from the app window goes.</summary>
public enum AppLink
{
    /// <summary>The app itself (its page, sign-in, a blank page): stays in the window.</summary>
    App,

    /// <summary>Another page of the app (the printable report): the browser, signed in with a single-use link.</summary>
    OwnPageInBrowser,

    /// <summary>A web page elsewhere: the browser.</summary>
    Outside,

    /// <summary>Another app (mail, Obsidian): only when the person clicked it.</summary>
    OutsideIfAsked,

    /// <summary>Never (data:, javascript:, file: and the like).</summary>
    Blocked,
}

/// <summary>What showing a path of the app in its window takes.</summary>
public sealed record AppOpen(string Kind, string? Value = null)
{
    public static AppOpen Focus() => new("focus");

    public static AppOpen SetHash(string hash) => new("hash", hash);

    public static AppOpen Navigate(string path) => new("navigate", path);
}

/// <summary>The app window's navigation rules (kept out of the window so they can be tested).</summary>
public static class AppNavigation
{
    public static AppLink Classify(Uri origin, string? url)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (url == "about:blank")
        {
            return AppLink.App;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return AppLink.Blocked;
        }

        if (uri.Scheme is "http" or "https" && Uri.Compare(uri, origin, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
        {
            return IsShell(uri) || uri.AbsolutePath.StartsWith("/api/launch", StringComparison.Ordinal) ? AppLink.App : AppLink.OwnPageInBrowser;
        }

        return uri.Scheme switch
        {
            "http" or "https" => AppLink.Outside,
            "mailto" or "obsidian" => AppLink.OutsideIfAsked,
            _ => AppLink.Blocked,
        };
    }

    /// <summary>
    /// Showing <paramref name="path"/> ("/", "/#/task/&lt;id&gt;", "/?connect=1") while the window is on
    /// <paramref name="currentUrl"/>: bring it forward, change the address in place (no reload, nothing typed is lost),
    /// or load it.
    /// </summary>
    public static AppOpen Plan(string? currentUrl, bool loaded, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var onShell = loaded && Uri.TryCreate(currentUrl, UriKind.Absolute, out var current) && IsShell(current);
        if (onShell && path == "/")
        {
            return AppOpen.Focus();
        }

        return onShell && path.StartsWith("/#", StringComparison.Ordinal) ? AppOpen.SetHash(path[1..]) : AppOpen.Navigate(path);
    }

    /// <summary>The path, query and fragment of a link (to open it in the browser with a sign-in link).</summary>
    public static string PathOf(string url) => new Uri(url).PathAndQuery + new Uri(url).Fragment;

    private static bool IsShell(Uri uri) => uri.AbsolutePath is "/" or "/index.html";
}
