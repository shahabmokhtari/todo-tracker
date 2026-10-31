using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TodoTracker.Core;

namespace TodoTracker.Server;

public sealed class ApiToken
{
    private readonly byte[] _bytes;

    public ApiToken(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
        _bytes = Encoding.UTF8.GetBytes(value);
    }

    public string Value { get; }

    public bool Matches(string? candidate) =>
        candidate is not null && CryptographicOperations.FixedTimeEquals(_bytes, Encoding.UTF8.GetBytes(candidate));

    public static ApiToken LoadOrCreate(TodoTrackerServerOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ApiToken))
        {
            return new ApiToken(options.ApiToken);
        }

        Directory.CreateDirectory(options.DataDirectory);
        var path = Path.Combine(options.DataDirectory, "api-token");
        if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: >= 32 } existing)
        {
            return new ApiToken(existing);
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllText(path, token);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return new ApiToken(token);
    }
}

/// <summary>
/// Local-server protection: loopback-only Host/remote checks (DNS rebinding), bearer or SameSite=Strict cookie auth,
/// a custom header on cookie-authenticated mutations (CSRF from other localhost origins), and strict browser headers.
/// </summary>
internal static class Security
{
    public const string CookieName = "tt_session";
    public const string ClientHeader = "X-TodoTracker-Client";
    public const string ActorHeader = "X-TodoTracker-Actor";
    private const string ActorKey = "tt.actor";
    private const string SessionKey = "tt.session";

    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "[::1]"];

    public static Actor ActorOf(HttpContext context) =>
        context.Items.TryGetValue(ActorKey, out var actor) && actor is Actor a ? a : Actor.User;

    /// <summary>
    /// The request comes from a session the app opened itself (see <see cref="AppSessions"/>), so from the person at this
    /// computer; not from something holding the API token (an AI agent, a script, another app).
    /// </summary>
    public static bool IsAppSession(HttpContext context) => context.Items.TryGetValue(SessionKey, out var session) && session is true;

    public static async Task Guard(HttpContext context, RequestDelegate next, TodoTrackerServerOptions options, ApiToken token)
    {
        var response = context.Response;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        response.Headers.XFrameOptions = "DENY";

        if (context.RequestServices.GetService<Plugins.PhoneListener>() is { } phones && phones.Serves(context))
        {
            await GuardPhone(context, next).ConfigureAwait(false);
            return;
        }

        if (options.LocalOnly && !IsLoopback(context))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var path = context.Request.Path;
        var isApi = path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
        var isMcp = path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase);
        if (!isApi && !isMcp)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        response.Headers.CacheControl = "no-store";
        var mutating = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method);
        var hasClientHeader = context.Request.Headers.ContainsKey(ClientHeader);

        if (path.Equals("/api/login", StringComparison.OrdinalIgnoreCase) || path.Equals(Plugins.BrowserExtensionPlugin.ClaimPath, StringComparison.OrdinalIgnoreCase))
        {
            // Signing in (and pairing the browser extension) needs no token, only the app's own header: a web page
            // can't send it to this server, so it can't do either.
            if (!hasClientHeader)
            {
                response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context).ConfigureAwait(false);
            return;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        var bearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization["Bearer ".Length..].Trim() : null;
        if (bearer is not null && token.Matches(bearer))
        {
            context.Items[ActorKey] = ParseActor(context.Request.Headers[ActorHeader].ToString());
        }
        else if (bearer is not null && context.RequestServices.GetService<PairedBrowsers>()?.Match(bearer) is not null
            && context.RequestServices.GetService<Plugins.PluginHost>()?.IsSwitchedOff(Plugins.BrowserExtensionPlugin.Id) != true)
        {
            // A paired browser reaches only what the extension does (see BrowserRoutes), always as the browser.
            if (isMcp || !BrowserMayReach(path, context.Request.Method.ToUpperInvariant()))
            {
                response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            context.Items[ActorKey] = new Actor(ActorKind.Browser);
        }
        else if (context.Request.Cookies[CookieName] is { } cookie
            && (context.RequestServices.GetService<AppSessions>()?.Check(cookie) is { Valid: true } session ? session : token.Matches(cookie) ? (Valid: true, Owner: false) : default) is { Valid: true } signedIn)
        {
            if (mutating && !hasClientHeader)
            {
                response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            context.Items[ActorKey] = Actor.User;

            // Only a session the app opened itself is the person at this computer (an older cookie holding the token, or
            // a sign-in with the token, could be anything that has the token).
            context.Items[SessionKey] = signedIn.Owner;
        }
        else
        {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The phone port: reached from the network, so none of the loopback rules apply (host name, cookies, the API token,
    /// MCP). Only a paired phone's own token gets in, only to what the phone apps do (see <see cref="PhoneRoutes"/>), and
    /// only while Phones and "Let phones connect" are on; the one thing without a token is trading a pairing code for one.
    /// </summary>
    private static async Task GuardPhone(HttpContext context, RequestDelegate next)
    {
        context.Response.Headers.CacheControl = "no-store";
        var services = context.RequestServices;
        var path = context.Request.Path;
        var method = context.Request.Method.ToUpperInvariant();

        // The port listens on every network: only this one and Tailscale get anywhere (also dropped before TLS).
        if (!Plugins.PhoneListener.MayConnect(context.Connection.RemoteIpAddress))
        {
            await Refuse(context, StatusCodes.Status403Forbidden, "Phones connect from the same network as this computer, or over Tailscale.").ConfigureAwait(false);
            return;
        }

        var claiming = method == "POST" && path.Equals(Plugins.PhonesPlugin.ClaimPath, StringComparison.OrdinalIgnoreCase);
        if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body)
        {
            // The claim is a code and a name; a task with its notes fits in far less than this.
            body.MaxRequestBodySize = claiming ? MaxPhoneClaimBytes : MaxPhoneRequestBytes;
        }

        if (context.Request.ContentLength > (claiming ? MaxPhoneClaimBytes : MaxPhoneRequestBytes))
        {
            await Refuse(context, StatusCodes.Status413PayloadTooLarge, "That's more than a phone sends.").ConfigureAwait(false);
            return;
        }

        if (services.GetService<Plugins.PluginHost>()?.IsSwitchedOff(Plugins.PhonesPlugin.Id) != false || services.GetService<Plugins.PhoneAccess>()?.On != true)
        {
            await Refuse(context, StatusCodes.Status403Forbidden, "Phones can’t connect to this computer right now. In Todo Tracker on it, open Settings › Phones and turn on “Let phones connect”.").ConfigureAwait(false);
            return;
        }

        if (claiming)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        var bearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization["Bearer ".Length..].Trim() : null;
        if (services.GetService<PairedPhones>()?.Match(bearer) is not { } phone)
        {
            await Refuse(context, StatusCodes.Status401Unauthorized, "This phone isn’t connected to Todo Tracker. On your computer, open Settings › Phones and connect it.").ConfigureAwait(false);
            return;
        }

        var value = path.Value ?? string.Empty;
        if (!PhoneRoutes.Any(r => r.Method == method && r.Path.IsMatch(value)))
        {
            await Refuse(context, StatusCodes.Status403Forbidden, "The phone app can’t do that. Use Todo Tracker on your computer.").ConfigureAwait(false);
            return;
        }

        // The person with the phone, said by name ("You (Shahab's iPhone)" in the timeline).
        context.Items[ActorKey] = new Actor(ActorKind.User, phone.Name);
        await next(context).ConfigureAwait(false);
    }

    private static Task Refuse(HttpContext context, int status, string message) =>
        Results.Problem(message, statusCode: status).ExecuteAsync(context);

    private const long MaxPhoneClaimBytes = 4 * 1024;
    private const long MaxPhoneRequestBytes = 1024 * 1024;

    private const string Id = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";

    // \A…\z: the whole path, nothing after it (not even a line break, which $ would let through).
    private static System.Text.RegularExpressions.Regex Route(string pattern) =>
        new($"\\A{pattern}/?\\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // An allow-list of what the phone apps do. Anything else (and every route added later) stays closed to phones.
    private static readonly (string Method, System.Text.RegularExpressions.Regex Path)[] PhoneRoutes =
    [
        ("GET", Route("/health")),
        ("GET", Route("/api/export")),
        ("GET", Route("/api/settings")),
        ("PUT", Route("/api/settings/theme")),
        ("POST", Route("/api/capture")),
        ("POST", Route("/api/items")),
        ("PATCH", Route($"/api/items/{Id}")),
        ("DELETE", Route($"/api/items/{Id}")),
        ("POST", Route($"/api/items/{Id}/(complete|reopen|archive|unarchive|notes|schedule|after|steps|reminders|move|reorder|plan)")),
        ("POST", Route($"/api/items/{Id}/reminders/{Id}/dismiss")),
        ("POST", Route("/api/now/(order|first)")),
        ("GET", Route("/api/energy")),
        ("PUT", Route("/api/energy")),
        ("POST", Route("/api/groups")),
        ("PATCH", Route($"/api/groups/{Id}")),
        ("PUT", Route($"/api/groups/{Id}")),
        ("DELETE", Route($"/api/groups/{Id}")),
        ("POST", Route("/api/pomodoro/(start|pause|resume|skip|reset|next)")),
        ("POST", Route("/api/timer/(start|stop)")),
    ];

    private static bool BrowserMayReach(PathString path, string method)
    {
        // An allow-list of what the extension does: what to do now, add, finish, snooze, and notes. Anything else
        // (and every route added later) stays closed to a paired browser.
        var value = path.Value ?? string.Empty;
        return BrowserRoutes.Any(r => r.Method == method && r.Path.IsMatch(value));
    }

    private static readonly (string Method, System.Text.RegularExpressions.Regex Path)[] BrowserRoutes =
    [
        ("GET", new("^/api/dashboard/?\\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)),
        ("POST", new("^/api/capture/?\\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)),
        ("POST", new("^/api/items/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/(complete|notes|schedule)/?\\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)),
    ];

    public static void SetSessionCookie(HttpResponse response, string secret) =>
        response.Cookies.Append(CookieName, secret, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = false,
            Path = "/",
            MaxAge = TimeSpan.FromDays(30),
            IsEssential = true,
        });

    internal static Actor ParseActor(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return Actor.User;
        }

        var value = header.Trim();
        if (value.StartsWith("agent:", StringComparison.OrdinalIgnoreCase))
        {
            return Actor.Agent(value[6..]);
        }

        return value.ToLowerInvariant() switch
        {
            "browser" or "browser-extension" => new Actor(ActorKind.Browser),
            "teams" => new Actor(ActorKind.Teams),
            "agent" => Actor.Agent("agent"),
            _ => Actor.User,
        };
    }

    private static bool IsLoopback(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && !IPAddress.IsLoopback(remote))
        {
            return false;
        }

        var host = context.Request.Host.Host;
        return LoopbackHosts.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase)) || host == "::1";
    }
}
