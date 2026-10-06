using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TodoTracker.Server;

/// <summary>A browser paired with Todo Tracker (the extension), as the app shows it: never its token.</summary>
public sealed record PairedBrowser(string Id, string Name, DateTimeOffset PairedAt, DateTimeOffset? LastUsed);

/// <summary>
/// Browsers paired through a pairing code. Each gets its own token (kept only as a hash), which reaches the task API
/// and nothing else, always counts as the browser, and can be taken back on its own.
/// </summary>
public sealed class PairedBrowsers
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private List<Entry> _entries;

    public PairedBrowsers(TodoTrackerServerOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        _path = Path.Combine(options.DataDirectory, "paired-browsers.json");
        _time = time ?? TimeProvider.System;
        _entries = Load();
    }

    public IReadOnlyList<PairedBrowser> List()
    {
        lock (_lock)
        {
            return [.. _entries.Select(e => new PairedBrowser(e.Id, e.Name, e.PairedAt, e.LastUsed))];
        }
    }

    /// <summary>Pairs a browser: returns its token (handed to it once, never stored).</summary>
    public (string Token, PairedBrowser Browser) Pair(string? name)
    {
        var token = "ttb_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var clean = new string([.. (name ?? string.Empty).Trim().Where(c => !char.IsControl(c)).Take(60)]);
        var entry = new Entry(Guid.NewGuid().ToString("N")[..12], clean.Length == 0 ? "Browser" : clean, Hash(token), _time.GetUtcNow(), null);
        lock (_lock)
        {
            // Saved first: if it can't be, nothing changes (no token handed out for a browser that isn't kept).
            List<Entry> next = [.. _entries, entry];
            Save(next);
            _entries = next;
        }

        return (token, new PairedBrowser(entry.Id, entry.Name, entry.PairedAt, null));
    }

    /// <summary>The paired browser this token belongs to, or null.</summary>
    public PairedBrowser? Match(string? token)
    {
        if (token is null || !token.StartsWith("ttb_", StringComparison.Ordinal))
        {
            return null;
        }

        var hash = Encoding.ASCII.GetBytes(Hash(token));
        lock (_lock)
        {
            var index = _entries.FindIndex(e => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(e.Hash), hash));
            if (index < 0)
            {
                return null;
            }

            // "Last used" is for the list: written now and then, not on every request.
            var now = _time.GetUtcNow();
            var entry = _entries[index];
            if (entry.LastUsed is not { } last || now - last > TimeSpan.FromMinutes(10))
            {
                _entries[index] = entry = entry with { LastUsed = now };
                try
                {
                    Save(_entries);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Only "last used": never fail a request over it (a later request tries again).
                }
            }

            return new PairedBrowser(entry.Id, entry.Name, entry.PairedAt, entry.LastUsed);
        }
    }

    public bool Revoke(string id)
    {
        lock (_lock)
        {
            var remaining = _entries.Where(e => e.Id != id).ToList();
            if (remaining.Count == _entries.Count)
            {
                return false;
            }

            // Saved first: a removal that couldn't be saved would come back after a restart.
            Save(remaining);
            _entries = remaining;
            return true;
        }
    }

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private List<Entry> Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path), Json) ?? [] : [];
        }
        catch (JsonException)
        {
            // Unreadable: no browser gets in (fail closed), and the file is kept aside rather than overwritten.
            try
            {
                File.Move(_path, _path + ".bad", overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Save(List<Entry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, Json));
        File.Move(temp, _path, overwrite: true);
    }

    private sealed record Entry(string Id, string Name, string Hash, DateTimeOffset PairedAt, DateTimeOffset? LastUsed);
}
