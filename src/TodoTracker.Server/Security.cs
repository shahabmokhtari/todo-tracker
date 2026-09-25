using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
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

    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "[::1]"];

    public static Actor ActorOf(HttpContext context) =>
        context.Items.TryGetValue(ActorKey, out var actor) && actor is Actor a ? a : Actor.User;

    public static async Task Guard(HttpContext context, RequestDelegate next, TodoTrackerServerOptions options, ApiToken token)
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

        if (path.Equals("/api/login", StringComparison.OrdinalIgnoreCase))
        {
            if (!hasClientHeader)
            {
                response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context).ConfigureAwait(false);
            return;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && token.Matches(authorization["Bearer ".Length..].Trim()))
        {
            context.Items[ActorKey] = ParseActor(context.Request.Headers[ActorHeader].ToString());
        }
        else if (token.Matches(context.Request.Cookies[CookieName]))
        {
            if (mutating && !hasClientHeader)
            {
                response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            context.Items[ActorKey] = Actor.User;
        }
        else
        {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    public static void SetSessionCookie(HttpResponse response, ApiToken token) =>
        response.Cookies.Append(CookieName, token.Value, new CookieOptions
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
