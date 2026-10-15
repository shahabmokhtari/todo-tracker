using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TodoTracker.Server;

/// <summary>
/// Browser sign-ins: each gets its own random secret (the cookie), never the API token. A session opened from a link the
/// app made itself (the sidebar, the tray, <c>tt</c>, the server's console) is the <b>owner's</b>: the person at this
/// computer. One opened by signing in with the API token, or from a link asked for over HTTP with it, isn't: anything
/// holding the token (an AI agent, a script) could do the same. Kept (hashed) in the data folder so sign-ins survive a
/// restart.
/// </summary>
public sealed class AppSessions
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private Dictionary<string, Session>? _sessions;

    public AppSessions(TodoTrackerServerOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        _path = Path.Combine(options.DataDirectory, "sessions.json");
        _time = time ?? TimeProvider.System;
    }

    private sealed record Session(bool Owner, DateTimeOffset Expires);

    /// <summary>A new session; its secret goes in the cookie.</summary>
    public string Create(bool owner)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        lock (_lock)
        {
            var all = Load();
            var now = _time.GetUtcNow();
            foreach (var old in all.Where(s => s.Value.Expires <= now).Select(s => s.Key).ToList())
            {
                all.Remove(old);
            }

            all[Hash(secret)] = new Session(owner, now + Lifetime);
            Save(all);
        }

        return secret;
    }

    /// <summary>Whether <paramref name="secret"/> is a live session, and whether it's the owner's.</summary>
    public (bool Valid, bool Owner) Check(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length > 200)
        {
            return (false, false);
        }

        lock (_lock)
        {
            return Load().TryGetValue(Hash(secret), out var session) && session.Expires > _time.GetUtcNow() ? (true, session.Owner) : (false, false);
        }
    }

    private Dictionary<string, Session> Load()
    {
        if (_sessions is not null)
        {
            return _sessions;
        }

        try
        {
            _sessions = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, Session>>(File.ReadAllText(_path), JsonSerializerOptions.Web) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _sessions = [];
        }

        return _sessions;
    }

    private void Save(Dictionary<string, Session> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(all, JsonSerializerOptions.Web));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory: sign-ins last until the app stops.
        }
    }

    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
