using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OneThing.Core;

namespace OneThing.Server;

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

    public static ApiToken LoadOrCreate(OneThingServerOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ApiToken))
        {
            return new ApiToken(options.ApiToken);
        }

        Directory.CreateDirectory(options.DataDirectory);
        var path = Path.Combine(options.DataDirectory, "api-token");
        if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: >= 32 } existing)
        {
            SecretFile.Tighten(path);
            return new ApiToken(existing);
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        SecretFile.Write(path, token);
        return new ApiToken(token);
    }
}

/// <summary>Files holding secrets (the API token, the owner key, sign-ins): readable only by this user from the start.</summary>
internal static class SecretFile
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Writes <paramref name="text"/> to <paramref name="path"/>, never readable by others even for a moment.</summary>
    public static void Write(string path, string text)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, text);
            return;
        }

        using (var stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = OwnerOnly }))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(text);
        }

        // A file that was already there keeps its mode when it's written over.
        Tighten(path);
    }

    /// <summary>Takes away what others could read or write in a file made before (by an older version).</summary>
    public static void Tighten(string path)
    {
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & ~OwnerOnly) != 0)
        {
            File.SetUnixFileMode(path, OwnerOnly);
        }
    }
}

/// <summary>
/// Local-server protection: loopback-only Host/remote checks (DNS rebinding), bearer or SameSite=Strict cookie auth,
/// a custom header on cookie-authenticated mutations (CSRF from other localhost origins), and strict browser headers.
/// </summary>
internal static class Security
{
    public const string CookieName = "onething_session";
    public const string ClientHeader = "X-OneThing-Client";
    public const string ActorHeader = "X-OneThing-Actor";
    private const string ActorKey = "onething.actor";
    private const string SessionKey = "onething.session";

    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "[::1]"];

    public static Actor ActorOf(HttpContext context) =>
        context.Items.TryGetValue(ActorKey, out var actor) && actor is Actor a ? a : Actor.User;

    /// <summary>
    /// The request comes from a session the app opened itself (see <see cref="AppSessions"/>), or from an app that sends
    /// the owner key with the token (the Mac app), so from the person at this computer; not from something holding only
    /// the API token (an AI agent, a script, another app).
    /// </summary>
    public static bool IsAppSession(HttpContext context) => context.Items.TryGetValue(SessionKey, out var session) && session is true;

    public static async Task Guard(HttpContext context, RequestDelegate next, OneThingServerOptions options, ApiToken token)
    {
        var response = context.Response;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        response.Headers.XFrameOptions = "DENY";

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
            // The owner key (only in the data folder, never handed out) on top of the token: an app of the person at
            // this computer, like the Mac app. The token alone could be anything that has it.
            var owner = context.RequestServices.GetService<OwnerKey>()?.Matches(context.Request.Headers[OwnerKey.Header].ToString()) == true;
            context.Items[ActorKey] = ParseActor(context.Request.Headers[ActorHeader].ToString(), owner);
            context.Items[SessionKey] = owner;
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

    private static bool BrowserMayReach(PathString path, string method)
    {
        // An allow-list of what the extension does: what to do now, which extras are on, add, finish, snooze, and
        // notes. Anything else (and every route added later) stays closed to a paired browser.
        var value = path.Value ?? string.Empty;
        return BrowserRoutes.Any(r => r.Method == method && r.Path.IsMatch(value));
    }

    private static readonly (string Method, System.Text.RegularExpressions.Regex Path)[] BrowserRoutes =
    [
        ("GET", new("^/api/dashboard/?\\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)),
        ("GET", new("^/api/plugins/?\\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)),
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

    /// <summary>Who made a change sent with the API token (the X-OneThing-Actor header); only the owner's own apps are you.</summary>
    internal static Actor ParseActor(string header, bool owner)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return owner ? Actor.User : Actor.Agent(ApiClient);
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
            _ => owner ? Actor.User : Actor.Agent(ApiClient),
        };
    }

    /// <summary>What a change made with only the API token, without saying who, is shown as.</summary>
    public const string ApiClient = "API client";

    /// <summary>
    /// Only the person at this computer (the app's own session, or an app with the owner key) may use these endpoints;
    /// anything holding only the API token (an AI agent, a script) gets 403 with <paramref name="refusal"/>.
    /// </summary>
    public static TBuilder OwnerOnly<TBuilder>(this TBuilder builder, string refusal)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) => IsAppSession(context.HttpContext)
            ? await next(context).ConfigureAwait(false)
            : Results.Problem(refusal, statusCode: StatusCodes.Status403Forbidden));

    /// <summary>Like <see cref="OwnerOnly"/>, for what changes things: reading stays open to the API token.</summary>
    public static TBuilder OwnerOnlyChanges<TBuilder>(this TBuilder builder, string refusal)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var method = context.HttpContext.Request.Method;
            return HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || IsAppSession(context.HttpContext)
                ? await next(context).ConfigureAwait(false)
                : Results.Problem(refusal, statusCode: StatusCodes.Status403Forbidden);
        });

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
