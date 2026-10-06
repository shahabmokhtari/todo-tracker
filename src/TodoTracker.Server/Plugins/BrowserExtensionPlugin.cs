using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace TodoTracker.Server.Plugins;

public sealed record PairingClaim(string? Code, string? Name = null);

/// <summary>
/// Short-lived pairing codes: Todo Tracker shows 6 digits, the browser extension trades them for its own token, so no
/// secret is copied by hand. One code at a time, single use, 5 minutes, and 5 wrong guesses void it.
/// </summary>
public sealed class PairingCodes(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private const int MaxFailures = 5;
    private readonly Lock _lock = new();
    private (string Code, DateTimeOffset Expires)? _current;
    private int _failures;
    private string? _pairedName;

    /// <summary>What the dashboard shows while it waits: "waiting", "paired" (with the browser's name), or "none".</summary>
    public (string State, string? Browser) Status()
    {
        lock (_lock)
        {
            if (_pairedName is not null)
            {
                return ("paired", _pairedName);
            }

            return _current is { } current && current.Expires > time.GetUtcNow() ? ("waiting", null) : ("none", null);
        }
    }

    public void Paired(string name)
    {
        lock (_lock)
        {
            _pairedName = name;
        }
    }

    public (string Code, DateTimeOffset Expires) Create()
    {
        lock (_lock)
        {
            var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            _current = (code, time.GetUtcNow() + Lifetime);
            _failures = 0;
            _pairedName = null;
            return _current.Value;
        }
    }

    public bool TryRedeem(string? code)
    {
        lock (_lock)
        {
            if (_current is not { } current || current.Expires <= time.GetUtcNow())
            {
                _current = null;
                return false;
            }

            if (code is { Length: 6 } && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(code), System.Text.Encoding.ASCII.GetBytes(current.Code)))
            {
                _current = null;
                return true;
            }

            if (++_failures >= MaxFailures)
            {
                _current = null;
            }

            return false;
        }
    }
}

/// <summary>
/// The browser extension (Edge, Chrome, Safari): a step-by-step setup in the dashboard, the extension to download for
/// each browser, and pairing with a code. Until the extension is in the stores, it's loaded unpacked.
/// </summary>
public sealed class BrowserExtensionPlugin : ITodoPlugin
{
    public const string ClaimPath = "/api/plugins/browser-extension/claim";

    public PluginInfo Info { get; } = new("browser-extension", "Browser extension", "Edge, Chrome and Safari: see what to do now, add tasks and save notes from the page you're on.");

    public string? WebModule => "/js/plugins/browser-extension.js";

    public void ConfigureServices(IServiceCollection services, TodoTrackerServerOptions options)
    {
        services.AddSingleton<PairingCodes>();
        services.AddSingleton<PairedBrowsers>();
    }

    public void MapEndpoints(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapGet("/", (TodoTrackerServerOptions options) => new { serverUrl = options.BaseUrl });
        group.MapPost("/pair", (PairingCodes codes) =>
        {
            var (code, expires) = codes.Create();
            return new { code, expiresAt = expires };
        });
        group.MapGet("/pair", (PairingCodes codes) =>
        {
            var (state, browser) = codes.Status();
            return new { state, browser };
        });

        // Anonymous (the extension has no token yet), but only with the extension's header: see Security.Guard.
        group.MapPost("/claim", (PairingClaim claim, PairingCodes codes, PairedBrowsers browsers) =>
        {
            if (!codes.TryRedeem(claim.Code?.Trim()))
            {
                return Results.Problem("That code didn’t work (codes last 5 minutes and work once). Get a new one in Todo Tracker.", statusCode: StatusCodes.Status400BadRequest);
            }

            var (token, browser) = browsers.Pair(claim.Name);
            codes.Paired(browser.Name);
            return Results.Ok(new { token });
        });

        group.MapGet("/devices", (PairedBrowsers browsers) => browsers.List());
        group.MapDelete("/devices/{id}", (string id, PairedBrowsers browsers) =>
            browsers.Revoke(id) ? Results.NoContent() : Results.NotFound());

        group.MapGet("/download/{browser}", (string browser) =>
        {
            var safari = browser switch
            {
                "chromium" => false,
                "safari" => true,
                _ => throw new ArgumentException("Choose chromium (Edge, Chrome) or safari.", nameof(browser)),
            };
            return Results.File(Package(safari), "application/zip", safari ? "todo-tracker-extension-safari.zip" : "todo-tracker-extension.zip");
        });
    }

    /// <summary>The extension's files as a zip; Safari gets its own manifest (a popup instead of the side panel).</summary>
    internal static byte[] Package(bool safari)
    {
        var assembly = typeof(BrowserExtensionPlugin).Assembly;
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var resource in assembly.GetManifestResourceNames())
            {
                var path = resource.Replace('\\', '/');
                if (!path.StartsWith("extension/", StringComparison.Ordinal))
                {
                    continue;
                }

                var name = path["extension/".Length..];
                if (name == "manifest.json" && safari)
                {
                    continue;
                }

                if (name.StartsWith("safari/", StringComparison.Ordinal))
                {
                    if (!(safari && name == "safari/manifest.json"))
                    {
                        continue;
                    }

                    name = "manifest.json";
                }

                using var source = assembly.GetManifestResourceStream(resource)!;
                using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                source.CopyTo(entry);
            }
        }

        return buffer.ToArray();
    }
}
