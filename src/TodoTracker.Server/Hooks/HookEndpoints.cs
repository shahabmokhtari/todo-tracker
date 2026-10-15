using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TodoTracker.Core;

namespace TodoTracker.Server.Hooks;

public sealed record HookTestRequest(string? Event = null);

/// <summary>
/// Settings › Hooks (what's set up, recent runs, try one) and the event stream. Nothing here changes hooks.json: what
/// runs is decided only by whoever can edit that file on this computer.
/// </summary>
internal static class HookEndpoints
{
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);

    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/hooks", (HookService hooks) => Results.Ok(hooks.Status()));

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
