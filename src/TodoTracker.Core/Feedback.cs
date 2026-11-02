using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace TodoTracker.Core;

/// <summary>What the person wrote in the feedback sheet: a kind (bug, idea, other), the note, an optional email.</summary>
/// <param name="Source">How the sheet was opened: button, shake, page or qr.</param>
/// <param name="ClientId">Made once per sheet and sent again on a retry, so a note is never filed twice.</param>
public sealed record FeedbackDraft(string Kind, string? Message, string? Contact, string Source, string ClientId);

/// <summary>Coarse facts about the app and device sent with feedback (never anything from the tasks, no ids).</summary>
public sealed record FeedbackDevice(string Platform, string? AppVersion, string? Build, string? Os, string? Device, string? Language, string? Screen);

/// <summary>What an answer from the relay means for the sheet.</summary>
public enum FeedbackOutcome
{
    /// <summary>Filed (or already filed): say thanks and close.</summary>
    Sent,

    /// <summary>Offline, too many notes or the relay is down: keep the draft and try again with the same id.</summary>
    Retry,

    /// <summary>Refused: say something went wrong.</summary>
    Failed,
}

/// <summary>
/// Send feedback: a note becomes a GitHub issue through the shared relay at issues.awesomehq.app, with its public key
/// (no GitHub token in any app). The same rules in Swift and Kotlin; tests/fixtures/feedback.json checks all of them.
/// </summary>
public static class Feedback
{
    public const string RelayUrl = "https://issues.awesomehq.app/v1/issues";

    /// <summary>The relay's public key for Onething: it can only open new issues in this repository, so it's not a secret.</summary>
    public const string PublicKey = "pk_gGPPMjRSsuHaXRdgdXvUevvL";

    public const int MaxMessage = 2000;

    public static IReadOnlyList<(string Id, string Label)> Kinds { get; } = [("bug", "Bug"), ("idea", "Idea"), ("other", "Other")];

    public static IReadOnlyList<string> Sources { get; } = ["button", "shake", "page", "qr"];

    /// <summary>The words every sheet shows.</summary>
    public static class Texts
    {
        public const string Sent = "Thanks — we read every note.";
        public const string Retry = "Couldn’t send — check your connection and try again.";
        public const string Failed = "Couldn’t send your note: something went wrong on our side. Please try again later.";
        public const string FinePrint = "We’ll include your app version, device model and language.";
        public const string ContactHint = "Only if you’d like a reply";

        public static string For(FeedbackOutcome outcome) => outcome switch
        {
            FeedbackOutcome.Sent => Sent,
            FeedbackOutcome.Retry => Retry,
            _ => Failed,
        };
    }

    /// <summary>
    /// The note as sent: control characters removed (new lines and tabs stay), trimmed, cut to
    /// <paramref name="maxMessage"/> code points and trimmed again. Empty: nothing to send.
    /// </summary>
    public static string CleanMessage(string? text, int maxMessage = MaxMessage)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var kept = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\n' or '\t' || !char.IsControl(c))
            {
                kept.Append(c);
            }
        }

        var trimmed = kept.ToString().Trim();
        var cut = new StringBuilder();
        var count = 0;
        foreach (var rune in trimmed.EnumerateRunes())
        {
            if (count++ == maxMessage)
            {
                break;
            }

            cut.Append(rune.ToString());
        }

        return cut.ToString().Trim();
    }

    /// <summary>How many code points the note has as it would be sent (for the counter near the limit).</summary>
    public static int Length(string? text) => CleanMessage(text, int.MaxValue).EnumerateRunes().Count();

    /// <summary>The request body for the relay, or null when there's nothing to send.</summary>
    /// <exception cref="ArgumentException">An unknown kind or source.</exception>
    public static JsonObject? Body(FeedbackDraft draft, FeedbackDevice device, int maxMessage = MaxMessage)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(device);
        var kind = Kinds.FirstOrDefault(k => k.Id == draft.Kind);
        if (kind.Id is null)
        {
            throw new ArgumentException($"Unknown kind of feedback: {draft.Kind}.", nameof(draft));
        }

        if (!Sources.Contains(draft.Source))
        {
            throw new ArgumentException($"Unknown source of feedback: {draft.Source}.", nameof(draft));
        }

        var message = CleanMessage(draft.Message, maxMessage);
        if (message.Length == 0)
        {
            return null;
        }

        var platform = device.Platform.ToLowerInvariant();
        var body = new JsonObject
        {
            ["message"] = message,
            ["title"] = $"{kind.Label} · {device.Platform}: {message}",
        };
        if (draft.Contact?.Trim() is { Length: > 0 } contact)
        {
            body["contact"] = contact;
        }

        body["clientID"] = draft.ClientId;
        body["labels"] = new JsonArray($"kind:{kind.Id}", $"platform:{platform}", $"source:{draft.Source}");
        var metadata = new JsonObject();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                metadata[key] = value;
            }
        }

        Add("Platform", platform);
        Add("App version", string.IsNullOrWhiteSpace(device.AppVersion) ? null : string.IsNullOrWhiteSpace(device.Build) ? device.AppVersion : $"{device.AppVersion} ({device.Build})");
        Add("OS", device.Os);
        Add("Device", device.Device);
        Add("Language", device.Language);
        Add("Screen", device.Screen);
        Add("Source", draft.Source);
        body["metadata"] = metadata;
        return body;
    }

    /// <summary>What the relay's answer means: 2xx sent; no answer, 408, 429 or 5xx try again; anything else failed.</summary>
    public static FeedbackOutcome Classify(int? status) => status switch
    {
        null or 408 or 429 or >= 500 and <= 599 => FeedbackOutcome.Retry,
        >= 200 and <= 299 => FeedbackOutcome.Sent,
        _ => FeedbackOutcome.Failed,
    };

    /// <summary>The platform name in titles and labels for the OS this process runs on.</summary>
    public static string ThisPlatform() =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "Web";

    /// <summary>"Windows 10.0.26100", "macOS 15.1", "Linux 6.8.0": the OS and its version, nothing more.</summary>
    public static string ThisOs()
    {
        var v = Environment.OSVersion.Version;
        var parts = new[] { v.Major, v.Minor, v.Build }.Where(p => p >= 0).Select(p => p.ToString(CultureInfo.InvariantCulture));
        return $"{ThisPlatform()} {string.Join('.', parts)}";
    }
}
