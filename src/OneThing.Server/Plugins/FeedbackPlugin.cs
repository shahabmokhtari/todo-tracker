using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OneThing.Core;

namespace OneThing.Server.Plugins;

/// <summary>A note from the web app's feedback sheet (the server adds the app version and the OS).</summary>
public sealed record FeedbackRequest(string? Kind, string? Message, string? Contact, string? ClientId, string? Source, string? Language = null, string? Screen = null);

/// <summary>Where feedback goes, and whether it really goes.</summary>
/// <param name="Send">Off in debug builds (the note is logged instead) unless <c>ONETHING_FEEDBACK_SEND=1</c>.</param>
public sealed record FeedbackSettings(Uri RelayUrl, bool Send)
{
    public static FeedbackSettings FromEnvironment() =>
#if DEBUG
        From(Environment.GetEnvironmentVariable, debugBuild: true);
#else
        From(Environment.GetEnvironmentVariable, debugBuild: false);
#endif

    /// <summary>
    /// <c>ONETHING_FEEDBACK_SEND</c>: 1 sends even from a debug build, 0 never sends. <c>ONETHING_FEEDBACK_URL</c>: another
    /// relay (a local stand-in for tests).
    /// </summary>
    public static FeedbackSettings From(Func<string, string?> env, bool debugBuild)
    {
        ArgumentNullException.ThrowIfNull(env);
        var send = env("ONETHING_FEEDBACK_SEND")?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" => true,
            "0" or "false" or "no" => false,
            _ => !debugBuild,
        };
        var url = Uri.TryCreate(env("ONETHING_FEEDBACK_URL"), UriKind.Absolute, out var other) && other.Scheme is "http" or "https"
            ? other
            : new Uri(Feedback.RelayUrl);
        return new FeedbackSettings(url, send);
    }
}

/// <summary>Sends a note to the issues relay with the app's public key (it can only open issues; no GitHub token here).</summary>
public sealed partial class FeedbackRelay(IHttpClientFactory http, FeedbackSettings settings, ILogger<FeedbackRelay> log)
{
    public const string HttpClientName = "feedback";

    public sealed record Answer(FeedbackOutcome Outcome, int? Status, string? Ref, string? State);

    public async Task<Answer> SendAsync(JsonObject body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!settings.Send)
        {
            // The reply address stays out of the log.
            var logged = body.DeepClone().AsObject();
            logged.Remove("contact");
            LogNotSent(log, settings.RelayUrl, logged);
            return new Answer(FeedbackOutcome.Sent, null, null, "logged");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.RelayUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Feedback.PublicKey);
        try
        {
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var outcome = Feedback.Classify(status);
            if (outcome != FeedbackOutcome.Sent)
            {
                LogRefused(log, status);
                return new Answer(outcome, status, null, null);
            }

            // Taken: the note is sent whatever the answer says (the ref and status are only shown when they're there).
            var answer = ReadAnswer(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return new Answer(outcome, status, Text(answer, "ref"), Text(answer, "status"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !cancellationToken.IsCancellationRequested)
        {
            LogOffline(log, ex.Message);
            return new Answer(FeedbackOutcome.Retry, null, null, null);
        }
    }

    private static JsonObject? ReadAnswer(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonObject? answer, string name) =>
        answer?[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    [LoggerMessage(Level = LogLevel.Information, Message = "Feedback not sent (a debug build; set ONETHING_FEEDBACK_SEND=1 to send it to {Url}): {Body}")]
    private static partial void LogNotSent(ILogger logger, Uri url, JsonObject body);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The issues relay answered {Status} to a feedback note.")]
    private static partial void LogRefused(ILogger logger, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't reach the issues relay: {Problem}")]
    private static partial void LogOffline(ILogger logger, string problem);
}

/// <summary>Send feedback: a bug, an idea or anything else, straight to the people who make One Thing (a GitHub issue).</summary>
public sealed partial class FeedbackPlugin : ITodoPlugin
{
    public const string Id = "feedback";

    public PluginInfo Info { get; } = new(Id, "Send feedback", "Tell us about a bug or an idea from Settings or the menu. It becomes an issue on One Thing’s GitHub, with only your app version, system and language.", Live: true);

    public string? WebModule => "/js/plugins/feedback.js";

    public void ConfigureServices(IServiceCollection services, OneThingServerOptions options)
    {
        services.TryAddSingleton(_ => FeedbackSettings.FromEnvironment());
        services.AddHttpClient(FeedbackRelay.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(20));
        services.AddSingleton<FeedbackRelay>();
    }

    public void MapEndpoints(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        // Only the person at this computer, not an agent or a script with the API token.
        group.AddEndpointFilter(async (context, next) => Security.IsAppSession(context.HttpContext)
            ? await next(context).ConfigureAwait(false)
            : Results.Problem("Only you can send feedback: from Settings in the One Thing app on this computer.", statusCode: StatusCodes.Status403Forbidden));

        group.MapPost("/", async (FeedbackRequest request, FeedbackRelay relay, CancellationToken ct) =>
        {
            if (Feedback.Kinds.All(k => k.Id != request.Kind) || request.Source is null || !Feedback.Sources.Contains(request.Source))
            {
                return Results.Problem("Pick Bug, Idea or Other.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (!Guid.TryParse(request.ClientId, out _))
            {
                return Results.Problem("Close the feedback window and open it again.", statusCode: StatusCodes.Status400BadRequest);
            }

            var device = new FeedbackDevice(Feedback.ThisPlatform(), McpInfo.Version, null, Feedback.ThisOs(),
                System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), Coarse(request.Language), Coarse(request.Screen));
            if (Feedback.Body(new FeedbackDraft(request.Kind!, request.Message, request.Contact, request.Source, request.ClientId!), device) is not { } body)
            {
                return Results.Problem("Write a few words first.", statusCode: StatusCodes.Status400BadRequest);
            }

            var answer = await relay.SendAsync(body, ct).ConfigureAwait(false);
            return answer.Outcome switch
            {
                FeedbackOutcome.Sent => Results.Json(new { @ref = answer.Ref, status = answer.State }, statusCode: StatusCodes.Status202Accepted),
                FeedbackOutcome.Retry => Results.Problem(Feedback.Texts.Retry, statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Problem(Feedback.Texts.Failed, statusCode: StatusCodes.Status422UnprocessableEntity),
            };
        });
    }

    /// <summary>A language tag or a screen size from the browser ("en-US", "1920x1080"); anything else is left out.</summary>
    private static string? Coarse(string? value) => value is not null && CoarseValue().IsMatch(value) ? value : null;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,23}$")]
    private static partial Regex CoarseValue();
}
