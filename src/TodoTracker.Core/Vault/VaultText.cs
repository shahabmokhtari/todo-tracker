using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TodoTracker.Core.Vault;

/// <summary>A task file that can't be understood safely; the store keeps the last good version and never writes it.</summary>
public sealed class VaultFormatException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>How dates are shown (local time zone), what "now" is (for timestamps the file doesn't have yet), and the
/// file's folder relative to the vault root (to resolve relative attachment links).</summary>
public sealed record TaskMarkdownContext(TimeZoneInfo TimeZone, DateTimeOffset Now, string FileDirectory);

/// <summary>Shared formatting for the markdown vault (dates, durations, YAML scalars, hidden JSON).</summary>
internal static partial class VaultText
{
    public const string DefaultDueTime = "17:00";
    public const string DefaultScheduledTime = "09:00";
    public const string DefaultDoneTime = "12:00";

    public static readonly JsonSerializerOptions HiddenJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset ToLocal(DateTimeOffset value, TimeZoneInfo tz) => TimeZoneInfo.ConvertTime(value, tz);

    /// <summary>Local date-time the way Obsidian's date &amp; time property writes it (seconds only when needed).</summary>
    public static string LocalDateTime(DateTimeOffset value, TimeZoneInfo tz)
    {
        var local = ToLocal(value, tz);
        return local.ToString(local.Second == 0 && local.Millisecond == 0 ? "yyyy-MM-dd'T'HH:mm" : "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static string LocalDate(DateTimeOffset value, TimeZoneInfo tz) => ToLocal(value, tz).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string LocalMinute(DateTimeOffset value, TimeZoneInfo tz) => ToLocal(value, tz).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static DateTimeOffset AtLocal(DateOnly date, TimeOnly time, TimeZoneInfo tz)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }

    public static DateTimeOffset AtLocal(DateOnly date, string time, TimeZoneInfo tz) =>
        AtLocal(date, TimeOnly.ParseExact(time, "HH:mm", CultureInfo.InvariantCulture), tz);

    public static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>ISO instant (with Z/offset), local date-time, or a date (at <paramref name="defaultTime"/>).</summary>
    public static DateTimeOffset? ParseTime(string? text, TimeZoneInfo tz, string defaultTime)
    {
        var value = text?.Trim().Trim('"', '\'');
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (HasZone().IsMatch(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var exact))
        {
            return exact;
        }

        if (ParseDate(value) is { } date)
        {
            return AtLocal(date, defaultTime, tz);
        }

        string[] formats = ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.fff", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss"];
        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return AtLocal(DateOnly.FromDateTime(local), TimeOnly.FromDateTime(local), tz);
        }

        throw new FormatException($"\"{text}\" isn't a date (use 2026-01-31, 2026-01-31T17:00, or an ISO time).");
    }

    /// <summary>Exact instants written by the app (hidden JSON); null when missing or unreadable.</summary>
    public static DateTimeOffset? ParseInstant(string? text) =>
        !string.IsNullOrWhiteSpace(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    public static string Duration(TimeSpan value)
    {
        var minutes = (long)Math.Round(value.TotalMinutes);
        return minutes % 1440 == 0 ? $"{minutes / 1440}d" : minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes}m";
    }

    public static TimeSpan? ParseDuration(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plainMinutes))
        {
            return TimeSpan.FromMinutes(plainMinutes);
        }

        var parts = DurationPart().Matches(value);
        if (parts.Count == 0 || string.Concat(parts.Select(p => p.Value.Replace(" ", string.Empty, StringComparison.Ordinal))).Length != value.Replace(" ", string.Empty, StringComparison.Ordinal).Length)
        {
            throw new FormatException($"\"{text}\" isn't a duration (use 90m, 24h, or 1d).");
        }

        var total = TimeSpan.Zero;
        foreach (Match part in parts)
        {
            var n = long.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture);
            total += part.Groups[2].Value.ToLowerInvariant() switch
            {
                "d" => TimeSpan.FromDays(n),
                "h" => TimeSpan.FromHours(n),
                _ => TimeSpan.FromMinutes(n),
            };
        }

        return total;
    }

    /// <summary>A YAML scalar: plain when unambiguous, otherwise a double-quoted (JSON-compatible) string.</summary>
    public static string YamlScalar(string value) =>
        SafePlain().IsMatch(value) && !LooksTyped().IsMatch(value) ? value : JsonSerializer.Serialize(value, HiddenJson);

    /// <summary>Compact JSON for <c>%%…%%</c> comments; a literal <c>%</c> is escaped so the comment can't end early.</summary>
    public static string Hidden<T>(T value) => JsonSerializer.Serialize(value, HiddenJson).Replace("%", "\\u0025", StringComparison.Ordinal);

    public static Actor ParseActor(string? text)
    {
        var value = text?.Trim() ?? string.Empty;
        if (value.Length == 0 || value.Equals("You", StringComparison.OrdinalIgnoreCase))
        {
            return Actor.User;
        }

        if (value.Equals("Edited in files", StringComparison.OrdinalIgnoreCase))
        {
            return new Actor(ActorKind.Vault);
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        var kindText = colon < 0 ? value : value[..colon];
        if (Enum.TryParse<ActorKind>(kindText.Trim(), ignoreCase: true, out var kind) && Enum.IsDefined(kind) && !int.TryParse(kindText, out _))
        {
            var name = colon < 0 ? null : value[(colon + 1)..].Trim();
            return new Actor(kind, string.IsNullOrEmpty(name) ? null : name);
        }

        return new Actor(ActorKind.User, value);
    }

    /// <summary>Stable, short Obsidian block id for a task (unless the file already gave it one).</summary>
    public static string BlockId(Guid id) => "t" + id.ToString("N")[^7..];

    [GeneratedRegex(@"(Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex HasZone();

    [GeneratedRegex(@"(\d+)\s*([dhm])", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPart();

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} _./+\-]*$")]
    private static partial Regex SafePlain();

    [GeneratedRegex(@"^(true|false|yes|no|on|off|null|~|[-+]?[\d._]+([eE][-+]?\d+)?|\d{4}-\d{2}-\d{2}.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex LooksTyped();
}
