using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TodoTracker.Core;

namespace TodoTracker.Server.Hooks;

public sealed record HookTestRequest(string? Event = null);

/// <summary>A hook to add or change: when (<see cref="Event"/>, or <c>*</c>) and what (<see cref="Hook"/>).</summary>
public sealed record HookRequest(string? Event, HookSpec? Hook);

public sealed record HookEnabledRequest(bool Enabled);

/// <summary>
/// Settings › Hooks: what's set up, adding and changing hooks, recent runs, trying one; and the event stream. What the
/// signed-in app changes runs at once; what anything with the API token (an AI agent, a script) adds or changes waits
/// until it's approved, which only the app (or tt on this computer) can do.
/// </summary>
internal static class HookEndpoints
{
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);

    /// <summary>The person in the app, or (anything else with the token) an agent whose changes need approval.</summary>
    internal static HookAuthor AuthorOf(HttpContext http) =>
        Security.IsAppSession(http) ? HookAuthor.You : new HookAuthor(Security.ActorOf(http) is { Kind: ActorKind.Agent, Name: { } name } ? name : "an app with the API token", Trusted: false);

    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(ApiEndpoints.MapDomainErrors);

        api.MapGet("/hooks", (HookService hooks) => Results.Ok(hooks.Status()));

        api.MapPost("/hooks", (HookRequest request, HttpContext http, HookStore store, TimeProvider time) =>
            Results.Ok(store.Add(request.Event ?? string.Empty, request.Hook ?? throw new ArgumentException("Say what the hook does (hook)."), AuthorOf(http), time.GetUtcNow())));

        api.MapPut("/hooks/{id}", (string id, HookRequest request, HttpContext http, HookStore store, TimeProvider time) =>
            Results.Ok(store.Update(id, request.Event, request.Hook ?? throw new ArgumentException("Say what the hook does (hook)."), AuthorOf(http), time.GetUtcNow())));

        api.MapPost("/hooks/{id}/enabled", (string id, HookEnabledRequest request, HookStore store) => Results.Ok(store.SetEnabled(id, request.Enabled)));

        api.MapPost("/hooks/{id}/approve", (string id, HttpContext http, HookStore store) => Results.Ok(store.Approve(id, AuthorOf(http))));

        api.MapDelete("/hooks/{id}", (string id, HookStore store) =>
        {
            store.Remove(id);
            return Results.NoContent();
        });

        // Runs the hooks for an event now, with an example task, and says how each went.
        api.MapPost("/hooks/test", async (HookTestRequest request, HookService hooks, IBoardStore store, TimeProvider time, CancellationToken cancellationToken) =>
        {
            var name = string.IsNullOrWhiteSpace(request.Event) ? "test" : request.Event.Trim();
            if (!EventNames.All.ContainsKey(name))
            {
                return Results.Problem($"\"{name}\" isn't an event.", statusCode: StatusCodes.Status400BadRequest);
            }

            var group = await store.ReadAsync(b => b.Groups[0].Name, cancellationToken).ConfigureAwait(false);
            var now = time.GetUtcNow();
            var task = new EventTask(Guid.Empty, "Example task", ["Example task"], group, [], [], "normal", null, name == "taskCompleted", null, null);
            var e = new TaskEvent(TaskEvent.CurrentVersion, $"test-{Guid.NewGuid():N}"[..17], name, now, new EventActor("user", null), "A test from Settings › Hooks", task);
            return Results.Ok(await hooks.RunAsync(e, cancellationToken).ConfigureAwait(false));
        });

        // Server-sent events: each one as `event: <name>` with the JSON in `data:`; `?events=a,b` picks some. A comment
        // every 20 s keeps the connection open through proxies.
        api.MapGet("/events", async (HttpContext http, HookService hooks, string? events) =>
        {
            var wanted = string.IsNullOrWhiteSpace(events) ? null : events.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
            var cancel = http.RequestAborted;
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            // Listening before saying "connected": nothing that happens after that is missed.
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            var stream = hooks.SubscribeAsync(stop.Token).GetAsyncEnumerator(stop.Token);
            var next = stream.MoveNextAsync().AsTask();
            try
            {
                await http.Response.WriteAsync(": connected\n\n", cancel).ConfigureAwait(false);
                await http.Response.Body.FlushAsync(cancel).ConfigureAwait(false);
                while (!cancel.IsCancellationRequested)
                {
                    var done = await Task.WhenAny(next, Task.Delay(KeepAlive, cancel)).ConfigureAwait(false);
                    if (done == next)
                    {
                        if (!await next.ConfigureAwait(false))
                        {
                            break;
                        }

                        var e = stream.Current;
                        next = stream.MoveNextAsync().AsTask();
                        if (wanted is not null && !wanted.Contains(e.Event))
                        {
                            continue;
                        }

                        await http.Response.WriteAsync($"id: {e.Id}\nevent: {e.Event}\ndata: {e.ToJson()}\n\n", cancel).ConfigureAwait(false);
                    }
                    else
                    {
                        await http.Response.WriteAsync(": keep-alive\n\n", cancel).ConfigureAwait(false);
                    }

                    await http.Response.Body.FlushAsync(cancel).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                // The client left.
            }
            finally
            {
                // The pending read has to end before the stream can be disposed.
                await stop.CancelAsync().ConfigureAwait(false);
                try
                {
                    await next.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The client left.
                }

                await stream.DisposeAsync().ConfigureAwait(false);
            }
        });
    }
}
