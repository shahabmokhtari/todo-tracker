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

    /// <summary>Connections the phone port takes at once (a household of phones needs a handful).</summary>
    public const int MaxConnections = 32;

    private int _connections;

    /// <summary>
    /// Whether a device at that address may use the phone port: this computer, its local network (10.x, 172.16–31.x,
    /// 192.168.x, link-local, IPv6 private and link-local) and Tailscale (100.64–127.x). The port listens on every
    /// network, so anything else (a public address, say on a café's or a hotel's network) is turned away.
    /// </summary>
    public static bool MayConnect(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }

        // fc00::/7 (unique local, Tailscale's IPv6 too) and fe80::/10 (link-local).
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (address.IsIPv6UniqueLocal || address.IsIPv6LinkLocal);
    }

    /// <summary>
    /// Kestrel connection middleware for the phone port: drops a connection from outside this network and Tailscale
    /// before TLS starts, and holds the port to <see cref="MaxConnections"/> at once.
    /// </summary>
    public Func<Microsoft.AspNetCore.Connections.ConnectionDelegate, Microsoft.AspNetCore.Connections.ConnectionDelegate> Gate() => next => async connection =>
    {
        if (!MayConnect((connection.RemoteEndPoint as IPEndPoint)?.Address))
        {
            connection.Abort();
            return;
        }

        if (Interlocked.Increment(ref _connections) > MaxConnections)
        {
            Interlocked.Decrement(ref _connections);
            connection.Abort();
            return;
        }

        try
        {
            await next(connection).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _connections);
        }
    };

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
                return X509CertificateLoader.LoadPkcs12FromFile(path, password: null, KeyFlags);
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
        return X509CertificateLoader.LoadPkcs12(bytes, password: null, KeyFlags);
    }

    /// <summary>
    /// How the private key is loaded for HTTPS: Windows (SChannel) needs a key it can use outside this process's memory,
    /// so not an ephemeral one; Linux keeps it in memory only; macOS can't load ephemeral keys, so it uses its default.
    /// </summary>
    private static X509KeyStorageFlags KeyFlags =>
        OperatingSystem.IsWindows() ? X509KeyStorageFlags.Exportable
        : OperatingSystem.IsLinux() ? X509KeyStorageFlags.EphemeralKeySet
        : X509KeyStorageFlags.DefaultKeySet;

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
/// nobody types them). Wrong codes slow down the address that sent them (10 a minute), and all claims together have a
/// loose limit (100 a minute), so guessing gets nowhere and another device on the network can't keep your phone out.
/// </summary>
public sealed class PhonePairingCodes(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>Wrong codes one address may send in a minute.</summary>
    public const int FailuresPerMinute = 10;

    /// <summary>Claims from everywhere together in a minute, right or wrong.</summary>
    public const int ClaimsPerMinute = 100;

    /// <summary>Longer than any code: refused without a look.</summary>
    public const int MaxCodeLength = 64;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly Lock _lock = new();
    private readonly Queue<DateTimeOffset> _claims = new();
    private readonly Dictionary<IPAddress, Queue<DateTimeOffset>> _failures = [];
    private (string Code, DateTimeOffset Expires, string Id)? _current;
    private (string Id, string Phone)? _paired;

    public enum Claim
    {
        Paired,
        Wrong,
        TooMany,

        /// <summary>The code was right but the phone couldn't be saved: the code still works.</summary>
        NotSaved,
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

    /// <summary>
    /// Pairs with <paramref name="pair"/> when the code is the one shown and still fresh, all at once: the code is used up
    /// only once the phone is saved (two claims at once pair one phone; one that couldn't be saved can try again).
    /// </summary>
    public Claim TryRedeem(string? code, IPAddress? from, Func<(string Token, string Name)> pair, out string? token)
    {
        ArgumentNullException.ThrowIfNull(pair);
        token = null;
        var who = from is null ? IPAddress.None : from.IsIPv4MappedToIPv6 ? from.MapToIPv4() : from;
        lock (_lock)
        {
            var now = time.GetUtcNow();
            Trim(_claims, now);
            foreach (var (address, failures) in _failures.ToList())
            {
                if (Trim(failures, now) == 0)
                {
                    _failures.Remove(address);
                }
            }

            if (_claims.Count >= ClaimsPerMinute || (_failures.TryGetValue(who, out var mine) && mine.Count >= FailuresPerMinute))
            {
                return Claim.TooMany;
            }

            _claims.Enqueue(now);
            if (_current is { } stale && stale.Expires <= now)
            {
                _current = null;
            }

            if (_current is not { } current || code is not { Length: > 0 and <= MaxCodeLength }
                || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes(current.Code)))
            {
                if (!_failures.TryGetValue(who, out var failed))
                {
                    _failures[who] = failed = new Queue<DateTimeOffset>();
                }

                failed.Enqueue(now);
                return Claim.Wrong;
            }

            try
            {
                var (made, name) = pair();
                token = made;
                _current = null;
                _paired = (current.Id, name);
                return Claim.Paired;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Claim.NotSaved;
            }
        }
    }

    private static int Trim(Queue<DateTimeOffset> times, DateTimeOffset now)
    {
        while (times.Count > 0 && now - times.Peek() >= Window)
        {
            times.Dequeue();
        }

        return times.Count;
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

    /// <summary>A phone's name, as kept (longer names are cut).</summary>
    public const int MaxNameLength = 60;

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

            var name = claim.Name is { Length: > MaxNameLength } longName ? longName[..MaxNameLength] : claim.Name;
            var result = codes.TryRedeem(claim.Code?.Trim(), http.Connection.RemoteIpAddress, () =>
            {
                var (made, phone) = phones.Pair(name);
                return (made, phone.Name);
            }, out var token);
            return result switch
            {
                PhonePairingCodes.Claim.Paired => Results.Ok(new { token, computer = PhoneLink.ComputerName() }),
                PhonePairingCodes.Claim.TooMany => Results.Problem("Too many tries. Wait a minute, then scan the code again.", statusCode: StatusCodes.Status429TooManyRequests),
                PhonePairingCodes.Claim.NotSaved => Results.Problem("Couldn’t save this phone on the computer just now. Try again in a moment (the code still works).", statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Problem("That code didn’t work (codes last 10 minutes and work once). On your computer, open Settings › Phones and connect again.", statusCode: StatusCodes.Status403Forbidden),
            };
        });
    }

    private static PhoneStatusDto Status(PhoneAccess access, PhoneListener listener) =>
        new(access.On, listener.Running, access.On != listener.Running, listener.Port, listener.Problem, PhoneLink.ComputerName(),
            listener.Running ? PhoneLink.Hosts(listener.Port) : [], listener.Fingerprint);
}
