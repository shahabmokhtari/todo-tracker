using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TodoTracker.Server;

/// <summary>
/// Short-lived, single-use codes that turn a browser visit into a cookie session. They keep the long-lived API token
/// (which also authorizes MCP) out of URLs, browser history, and history sync.
/// </summary>
public sealed class LaunchCodes(TimeProvider time)
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);
    private const int MaxOutstanding = 256;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _codes = new(StringComparer.Ordinal);

    public string Create(TimeSpan? lifetime = null)
    {
        var now = time.GetUtcNow();
        foreach (var expired in _codes.Where(c => c.Value <= now).Select(c => c.Key).ToList())
        {
            _codes.TryRemove(expired, out _);
        }

        if (_codes.Count >= MaxOutstanding)
        {
            throw new InvalidOperationException("Too many outstanding launch links.");
        }

        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _codes[code] = now + (lifetime ?? DefaultLifetime);
        return code;
    }

    public bool TryRedeem(string? code) =>
        code is not null && _codes.TryRemove(code, out var expires) && expires > time.GetUtcNow();
}
