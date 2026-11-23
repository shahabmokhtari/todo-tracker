using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using OneThing.Server;
using OneThing.Server.Plugins;

namespace OneThing.Cli.Tests.Reach;

/// <summary>
/// The endpoints the real server maps, read from its endpoint data sources (not a hand-written list): the app built
/// as it runs, on a temporary data folder, with every extra switched on.
/// </summary>
internal static partial class MappedRoutes
{
    private static readonly Lazy<List<string>> Routes = new(Read);

    /// <summary>"GET /api/items/{id}", one per method and route, sorted.</summary>
    public static IReadOnlyList<string> All => Routes.Value;

    private static List<string> Read()
    {
        var data = Path.Combine(Path.GetTempPath(), "onething-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "plugins.json"), "{" + string.Join(",", PluginHost.BuiltIn.Select(p => $"\"{p.Info.Id}\": true")) + "}");
        var options = new OneThingServerOptions
        {
            DataDirectory = data,
            VaultPath = Path.Combine(data, "vault"),
            ApiToken = "coverage-token-0123456789abcdef",
            TimeZone = TimeZoneInfo.Utc,
            EnableBackgroundLoop = false,
            WatchVault = false,
            EnableHistory = false,
            LockDirectory = Path.Combine(data, "locks"),
        };
        // Never this computer's real OneDrive or iCloud Drive (kept off for every test here, as CliHarness does).
        Environment.SetEnvironmentVariable("ONETHING_CLOUD", "off");
        Environment.SetEnvironmentVariable("ONETHING_VAULT", null);
        try
        {
            var app = OneThingHost.Build(OneThingHost.CreateBuilder(options));
            try
            {
                return ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
                    .SelectMany(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods ?? ["ANY"])
                        .Select(m => $"{m.ToUpperInvariant()} /{Normalize(e.RoutePattern.RawText ?? string.Empty)}"))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToList();
            }
            finally
            {
                ((IDisposable)app).Dispose();
            }
        }
        finally
        {
            CliHarness.ForgetDefaultLock(options.VaultPath);
            try
            {
                Directory.Delete(data, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>/api/items/{id:guid}/ → api/items/{id} (no constraints, no slashes at the ends).</summary>
    private static string Normalize(string raw) => RouteConstraint().Replace(raw, "{$1}").Trim('/');

    [GeneratedRegex(@"\{([^}:?=]+)[^}]*\}")]
    private static partial Regex RouteConstraint();
}
