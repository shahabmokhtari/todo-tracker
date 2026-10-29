using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TodoTracker.Server.Plugins;

public sealed record PhoneClaim(string? Code, string? Name = null);

public sealed record PhoneAccessRequest(bool On);

/// <summary>What Settings › Phones shows: whether phones may connect, and whether this run listens for them.</summary>
/// <param name="LetPhonesConnect">The owner's choice.</param>
/// <param name="Listening">The phone port is open in this run (it opens and closes when the app starts).</param>
/// <param name="RestartRequired">The choice and this run differ: a restart opens (or closes) the port.</param>
/// <param name="Problem">Why the port couldn't open (in use, no certificate), said for the user.</param>
/// <param name="Addresses">Where phones reach this computer (host:port on the network and on Tailscale).</param>
/// <param name="Fingerprint">The phone port's certificate (base64url SHA-256), which phones check.</param>
public sealed record PhoneStatusDto(bool LetPhonesConnect, bool Listening, bool RestartRequired, int Port, string? Problem, string Computer, IReadOnlyList<string> Addresses, string? Fingerprint);

/// <summary>A code shown to pair a phone: as a QR code and a link the phone app opens.</summary>
public sealed record PhonePairingDto(string Id, string Code, DateTimeOffset ExpiresAt, string Link, string QrSvg, IReadOnlyList<string> Addresses, string Computer, string Fingerprint);

/// <summary>"Let phones connect" (<c>phones.json</c> in the data folder): only the person at this computer changes it.</summary>
public sealed class PhoneAccess
{
    private readonly string _path;
    private readonly Lock _lock = new();
    private bool? _on;

    public PhoneAccess(TodoTrackerServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _path = PathIn(options.DataDirectory);
    }

    public bool On
    {
        get
        {
            lock (_lock)
            {
                return _on ??= Read(_path);
            }
        }
    }

    public void Set(bool on)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, new JsonObject { ["letPhonesConnect"] = on }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            _on = on;
        }
    }

    /// <summary>Whether it's on in that data folder (read once at the start, before there's a host).</summary>
    public static bool IsOn(string dataDirectory) => Read(PathIn(dataDirectory));

    private static string PathIn(string dataDirectory) => Path.Combine(dataDirectory, "phones.json");

    private static bool Read(string path)
    {
        try
        {
            return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o && o["letPhonesConnect"] is JsonValue v && v.TryGetValue<bool>(out var on) && on;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable: phones stay out (fail closed).
            return false;
        }
    }
}

/// <summary>
/// The phone port, as decided when the app started: open (all network interfaces, HTTPS with this computer's own
/// certificate) only when the Phones extra and "Let phones connect" are both on. Requests on it follow the phone rules
/// in <see cref="Security"/>, never the loopback ones.
/// </summary>
public sealed class PhoneListener
{
    private PhoneListener(int port, X509Certificate2? certificate, string? problem)
    {
        Port = port;
        Certificate = certificate;
        Fingerprint = certificate is null ? null : PhoneCertificate.Fingerprint(certificate);
        Problem = problem;
    }

    public int Port { get; }

    /// <summary>The certificate the port uses; null when it isn't open.</summary>
    public X509Certificate2? Certificate { get; }

    public string? Fingerprint { get; }

    public string? Problem { get; }

    public bool Running => Certificate is not null;

    public static PhoneListener Closed(int port, string? problem = null) => new(port, null, problem);

    /// <summary>Whether this request came in on the phone port.</summary>
    public bool Serves(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Running && context.Connection.LocalPort == Port;
    }

    /// <summary>Decides at the start whether to open the phone port, and gets it ready (its certificate).</summary>
    public static PhoneListener Prepare(TodoTrackerServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!PluginHost.IsEnabled(options.DataDirectory, PhonesPlugin.Id) || !PhoneAccess.IsOn(options.DataDirectory))
        {
            return Closed(options.PhonePort);
        }

        if (options.PhonePort == options.Port)
        {
            return Closed(options.PhonePort, $"Phones need a port of their own, and {options.PhonePort} is the app's. Choose another phone port.");
        }

        try
        {
            // A port another app holds would stop the whole app from starting: phones just can't connect then.
            using var probe = new TcpListener(IPAddress.Any, options.PhonePort);
            probe.Start();
            probe.Stop();
        }
        catch (SocketException)
        {
            return Closed(options.PhonePort, $"Port {options.PhonePort} is used by another app, so phones can't connect. Close that app, then restart Todo Tracker.");
        }

        try
        {
            return new PhoneListener(options.PhonePort, PhoneCertificate.LoadOrCreate(options.DataDirectory), null);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return Closed(options.PhonePort, $"Couldn't make the certificate phones check ({ex.Message}). Restart Todo Tracker to try again.");
        }
    }
}

/// <summary>
/// This computer's own certificate for the phone port: made once and kept in the data folder (only this user can read
/// it). Phones trust it by its fingerprint, which comes in the pairing link, so no certificate authority is involved.
/// </summary>
public static class PhoneCertificate
{
    public const string FileName = "phone-certificate.pfx";

    public static X509Certificate2 LoadOrCreate(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        if (File.Exists(path))
        {
            try
            {
                return X509CertificateLoader.LoadPkcs12FromFile(path, password: null);
            }
            catch (CryptographicException)
            {
                // Unreadable: kept aside, and a new one made (paired phones then connect again).
                File.Move(path, path + ".bad", overwrite: true);
            }
        }

        Directory.CreateDirectory(dataDirectory);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Todo Tracker", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var now = DateTimeOffset.UtcNow;
        using var made = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));
        var bytes = made.Export(X509ContentType.Pkcs12);

        // Written private from the start (never readable by others, even for a moment), then moved into place.
        var temp = path + ".tmp";
        var file = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            file.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, file))
        {
            stream.Write(bytes);
        }

        File.Move(temp, path, overwrite: true);
        return X509CertificateLoader.LoadPkcs12(bytes, password: null);
    }

    /// <summary>The certificate's fingerprint as phones check it: SHA-256 of its DER bytes, base64url without padding.</summary>
    public static string Fingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Base64Url(SHA256.HashData(certificate.RawData));
    }

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The pairing link (and its QR code) a phone opens: where to reach this computer, the code, and the fingerprint.</summary>
public static class PhoneLink
{
    private static readonly Lazy<string> Name = new(FindComputerName);

    /// <summary>
    /// <c>todotracker://pair?v=1&amp;hosts=192.168.1.20:5318,100.101.102.103:5318&amp;code=…&amp;fp=…&amp;name=…</c>
    /// </summary>
    public static string Build(IReadOnlyList<string> hosts, string code, string fingerprint, string computer)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        return $"todotracker://pair?v=1&hosts={string.Join(',', hosts)}&code={Uri.EscapeDataString(code)}&fp={Uri.EscapeDataString(fingerprint)}&name={Uri.EscapeDataString(computer).Replace("'", "%27", StringComparison.Ordinal)}";
    }

    /// <summary>Where phones reach this computer now: each network (LAN) and Tailscale address, with the phone port.</summary>
    public static IReadOnlyList<string> Hosts(int port)
    {
        var found = new List<IPAddress>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    found.AddRange(adapter.GetIPProperties().UnicastAddresses.Select(a => a.Address));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No addresses to offer: Settings › Phones says so.
        }

        return [.. Pick(found).Select(a => $"{a}:{port}")];
    }

    /// <summary>Network addresses (10.x, 172.16–31.x, 192.168.x) first, then Tailscale's (100.64–127.x); no others, no repeats.</summary>
    public static IReadOnlyList<IPAddress> Pick(IEnumerable<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var v4 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Distinct().ToList();
        return [.. v4.Where(IsLan), .. v4.Where(IsTailscale)];
    }

    private static bool IsLan(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    // Tailscale gives each device an address in 100.64.0.0/10 (the shared address space).
    private static bool IsTailscale(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    /// <summary>The link as a QR code, an SVG image (shown as an image, so nothing in it runs).</summary>
    public static string Qr(string text)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        return new QRCoder.SvgQRCode(data).GetGraphic(8, "#000000", "#ffffff", drawQuietZones: true, sizingMode: QRCoder.SvgQRCode.SizingMode.ViewBoxAttribute);
    }

    /// <summary>This computer's name as its owner sees it (the Mac's "Computer Name"; elsewhere the machine name).</summary>
    public static string ComputerName() => Name.Value;

    private static string FindComputerName()
    {
        if (OperatingSystem.IsMacOS() && File.Exists("/usr/sbin/scutil"))
        {
            try
            {
                using var scutil = Process.Start(new ProcessStartInfo("/usr/sbin/scutil", "--get ComputerName") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
                if (scutil is not null)
                {
                    var name = scutil.StandardOutput.ReadToEnd().Trim();
                    if (scutil.WaitForExit(2000) && scutil.ExitCode == 0 && name.Length > 0)
                    {
                        return name;
                    }
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                // The machine name will do.
            }
        }

        return Environment.MachineName;
    }
}

/// <summary>
/// Codes that pair a phone: one at a time, single use, 10 minutes. They're long and random (they travel in the QR code,
/// nobody types them), and claims are limited to a few a minute so guessing gets nowhere.
/// </summary>
public sealed class PhonePairingCodes(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>Claims allowed in a minute, right or wrong.</summary>
    public const int ClaimsPerMinute = 10;

    private readonly Lock _lock = new();
    private readonly Queue<DateTimeOffset> _claims = new();
    private (string Code, DateTimeOffset Expires, string Id)? _current;
    private (string Id, string Phone)? _paired;

    public enum Claim
    {
        Paired,
        Wrong,
        TooMany,
    }

    public (string Code, DateTimeOffset Expires, string Id) Create()
    {
        lock (_lock)
        {
            _current = (PhoneCertificate.Base64Url(RandomNumberGenerator.GetBytes(18)), time.GetUtcNow() + Lifetime, Guid.NewGuid().ToString("N"));
            _paired = null;
            return _current.Value;
        }
    }

    /// <summary>"waiting", "paired" (with the phone's name) or "none" (expired, or replaced) for the code with that id.</summary>
    public (string State, string? Phone) Status(string? id)
    {
        lock (_lock)
        {
            id ??= _current?.Id ?? _paired?.Id;
            if (_paired is { } paired && paired.Id == id)
            {
                return ("paired", paired.Phone);
            }

            return _current is { } current && current.Id == id && current.Expires > time.GetUtcNow() ? ("waiting", null) : ("none", null);
        }
    }

    /// <summary>Uses the code up when it's the one shown and still fresh; <paramref name="codeId"/> is its id.</summary>
    public Claim TryRedeem(string? code, out string? codeId)
    {
        codeId = null;
        lock (_lock)
        {
            var now = time.GetUtcNow();
            while (_claims.Count > 0 && now - _claims.Peek() >= TimeSpan.FromMinutes(1))
            {
                _claims.Dequeue();
            }

            if (_claims.Count >= ClaimsPerMinute)
            {
                return Claim.TooMany;
            }

            _claims.Enqueue(now);
            if (_current is not { } current || current.Expires <= now)
            {
                _current = null;
                return Claim.Wrong;
            }

            if (code is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes(current.Code)))
            {
                return Claim.Wrong;
            }

            _current = null;
            codeId = current.Id;
            return Claim.Paired;
        }
    }

    public void Paired(string codeId, string phone)
    {
        lock (_lock)
        {
            _paired = (codeId, phone);
        }
    }
}

/// <summary>
/// Phones: the iPhone, iPad and Android apps use the tasks on this computer (the synced tasks folder) over the local
/// network or Tailscale. Off by default; once on, the owner also turns on "Let phones connect" and pairs each phone.
/// </summary>
public sealed class PhonesPlugin : ITodoPlugin
{
    public const string Id = "phones";

    /// <summary>Where a phone trades its pairing code for its own token (on the phone port only, without a token).</summary>
    public const string ClaimPath = "/api/phones/claim";

    public PluginInfo Info { get; } = new(Id, "Phones", "Use the iPhone, iPad and Android apps with the tasks on this computer, over your Wi-Fi or Tailscale.", DefaultEnabled: false, Live: true);

    public void ConfigureServices(IServiceCollection services, TodoTrackerServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton<PhonePairingCodes>();
        services.AddSingleton<PairedPhones>();
        services.AddSingleton<PhoneAccess>();

        // The app's own host decides at the start (TodoTrackerHost.CreateBuilder); anything else never opens the port.
        services.TryAddSingleton(PhoneListener.Closed(options.PhonePort));
    }

    public void MapEndpoints(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        // Who may reach this computer from the network: only the person at it (the app's own session), like hooks.
        group.AddEndpointFilter(async (context, next) => Security.IsAppSession(context.HttpContext)
            ? await next(context).ConfigureAwait(false)
            : Results.Problem("Only you can connect phones: in Settings › Phones of the Todo Tracker app on this computer.", statusCode: StatusCodes.Status403Forbidden));

        group.MapGet("/", (PhoneAccess access, PhoneListener listener) => Status(access, listener));
        group.MapPut("/access", (PhoneAccessRequest request, PhoneAccess access, PhoneListener listener) =>
        {
            access.Set(request.On);
            return Status(access, listener);
        });

        group.MapPost("/pair", (PhoneAccess access, PhoneListener listener, PhonePairingCodes codes) =>
        {
            if (!access.On || listener.Fingerprint is not { } fingerprint)
            {
                return Results.Problem(
                    access.On ? listener.Problem ?? "Restart Todo Tracker first, so phones can reach it." : "Turn on “Let phones connect” first.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var (code, expires, id) = codes.Create();
            var hosts = PhoneLink.Hosts(listener.Port);
            var computer = PhoneLink.ComputerName();
            var link = PhoneLink.Build(hosts, code, fingerprint, computer);
            return Results.Ok(new PhonePairingDto(id, code, expires, link, PhoneLink.Qr(link), hosts, computer, fingerprint));
        });
        group.MapGet("/pair", (string? id, PhonePairingCodes codes) =>
        {
            var (state, phone) = codes.Status(id);
            return new { state, phone };
        });

        group.MapGet("/devices", (PairedPhones phones) => phones.List());
        group.MapDelete("/devices/{id}", (string id, PairedPhones phones) =>
            phones.Revoke(id) ? Results.NoContent() : Results.NotFound());
    }

    /// <summary>The claim, outside the plugin's owner-only routes: a phone has no token yet (see Security.Guard).</summary>
    internal static void MapClaim(WebApplication app, PluginHost host)
    {
        app.MapPost(ClaimPath, (PhoneClaim claim, HttpContext http, PhoneListener listener, PhonePairingCodes codes, PairedPhones phones) =>
        {
            if (!listener.Serves(http) || host.IsSwitchedOff(Id))
            {
                return Results.NotFound();
            }

            switch (codes.TryRedeem(claim.Code?.Trim(), out var codeId))
            {
                case PhonePairingCodes.Claim.TooMany:
                    return Results.Problem("Too many tries. Wait a minute, then scan the code again.", statusCode: StatusCodes.Status429TooManyRequests);
                case PhonePairingCodes.Claim.Wrong:
                    return Results.Problem("That code didn’t work (codes last 10 minutes and work once). On your computer, open Settings › Phones and connect again.", statusCode: StatusCodes.Status403Forbidden);
            }

            var (token, phone) = phones.Pair(claim.Name);
            codes.Paired(codeId!, phone.Name);
            return Results.Ok(new { token, computer = PhoneLink.ComputerName() });
        });
    }

    private static PhoneStatusDto Status(PhoneAccess access, PhoneListener listener) =>
        new(access.On, listener.Running, access.On != listener.Running, listener.Port, listener.Problem, PhoneLink.ComputerName(),
            listener.Running ? PhoneLink.Hosts(listener.Port) : [], listener.Fingerprint);
}
