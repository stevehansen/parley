using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Parley.Mcp;
using Parley.Update;

namespace Parley.Hub;

/// <summary>
/// The hub process: one <see cref="CollabHub"/> behind a loopback HTTP API.
///
///   POST /mcp                      MCP Streamable HTTP (see <see cref="HttpMcpEndpoint"/>)
///   GET  /api/health               liveness probe used by the shim before auto-starting a hub
///   GET  /api/topics | /api/sessions | /api/messages?topic=&amp;count=
///   POST /api/messages             {session, topic, content} — send without MCP (UIs, scripts)
///   DELETE /api/topics/{name}      delete a topic and its messages
///   POST /api/update               install the newest release (restarts the hub)
///   POST /api/shutdown             stop, saving state first (how `parley update` stops the hub)
///   GET  /                         the web UI
///   GET  /api/events               SSE. With ?session=X: the messages X should receive (its topics,
///                                  not its own), optionally replaying ids after ?since=N; the
///                                  stream also counts as X being connected. Without: every message
///                                  plus coalesced "changed" signals for dashboards.
///
/// Only loopback Host headers are served (a DNS-rebinding page can't reach the hub), and every
/// state-changing endpoint takes a JSON body, which a cross-site page can't send without a CORS
/// preflight the hub never approves — no web page can post into agents' conversations.
/// </summary>
public static class HubServer
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(25);

    public static WebApplication Build(CollabHub hub, int port, string bindAddress = "127.0.0.1", UpdateChecker? updates = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://{bindAddress}:{port}");
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.PropertyNameCaseInsensitive = true;
        });

        var app = builder.Build();
        var mcp = new HttpMcpEndpoint(hub);

        app.Use(async (ctx, next) =>
        {
            if (!IsLoopbackHost(ctx.Request.Host.Host))
            {
                ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return;
            }
            await next();
        });

        app.MapGet("/", () => Results.Content(WebUi.Html, "text/html; charset=utf-8"));
        app.MapGet("/api/health", () => Results.Ok(new
        {
            name = "parley",
            version = Protocol.Version,
            latest = updates?.Latest?.ToString(3),
            updateAvailable = updates?.UpdateAvailable ?? false,
            pid = Environment.ProcessId, // lets `parley update` stop a hub the service doesn't own
        }));
        app.MapGet("/api/topics", () => Results.Ok(hub.GetTopics()));
        app.MapGet("/api/sessions", () => Results.Ok(hub.GetSessions()));
        app.MapGet("/api/messages", (string? topic, int? count) =>
            Results.Ok(hub.GetRecentMessages(Math.Clamp(count ?? 50, 1, 500), topic)));
        app.MapPost("/api/messages", (SendRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Session) || string.IsNullOrWhiteSpace(req.Topic) || string.IsNullOrEmpty(req.Content))
                return Results.BadRequest(new { error = "session, topic and content are required" });
            return Results.Ok(hub.SendMessage(req.Session.Trim(), req.Topic.Trim(), req.Content, subscribeSender: false));
        });
        app.MapDelete("/api/topics/{name}", (string name) =>
            hub.DeleteTopic(name) ? Results.NoContent() : Results.NotFound());
        app.MapPost("/api/shutdown", (ShutdownRequest _) =>
        {
            app.Lifetime.StopApplication();
            return Results.Accepted();
        });
        app.MapPost("/api/update", (UpdateRequest _) =>
        {
            if (updates is not { UpdateAvailable: true })
                return Results.Conflict(new { error = "No update available" });
            UpdateCommand.StartDetached();
            return Results.Accepted();
        });

        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            if (ctx.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
            {
                ctx.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                return;
            }
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);
            var result = await mcp.HandleAsync(body,
                ctx.Request.Headers["X-Session"].FirstOrDefault(),
                ctx.Request.Headers["Mcp-Session-Id"].FirstOrDefault(),
                ctx.Request.Headers["X-Working-Dir"].FirstOrDefault(),
                ctx.RequestAborted);

            ctx.Response.StatusCode = result.StatusCode;
            if (result.McpSessionId != null) ctx.Response.Headers["Mcp-Session-Id"] = result.McpSessionId;
            if (result.Body != null)
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(result.Body, ctx.RequestAborted);
            }
        });
        // No server-initiated stream on /mcp: push goes through /api/events and the stdio shim.
        app.MapGet("/mcp", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));

        app.MapGet("/api/events", (HttpContext ctx, string? session, int? since) =>
            StreamEventsAsync(ctx, hub, string.IsNullOrWhiteSpace(session) ? null : session.Trim(), since,
                app.Lifetime.ApplicationStopping));

        app.Lifetime.ApplicationStopping.Register(hub.Dispose);
        return app;
    }

    public sealed record SendRequest(string? Session, string? Topic, string? Content);

    public sealed record UpdateRequest;

    public sealed record ShutdownRequest;

    private static bool IsLoopbackHost(string host) =>
        host is "localhost" or "127.0.0.1" or "[::1]" or "::1";

    private static async Task StreamEventsAsync(HttpContext ctx, CollabHub hub, string? session, int? since,
        CancellationToken stopping)
    {
        // Streams never end on their own; without the stopping token shutdown waits them out.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, stopping);
        var ct = linked.Token;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        var queue = Channel.CreateUnbounded<Message?>(); // null = "state changed"
        var changedPending = 0;
        void OnMessage(Message m)
        {
            if (session == null || hub.IsDeliverable(session, m)) queue.Writer.TryWrite(m);
        }
        void OnChanged()
        {
            if (Interlocked.Exchange(ref changedPending, 1) == 0) queue.Writer.TryWrite(null);
        }

        // Baseline first, then subscribe, then replay: anything sent in between is queued, and ids
        // dedupe the overlap. "ready" tells a reconnecting client where to resume from if nothing
        // arrives before the stream drops.
        var lastSent = since ?? hub.LastMessageId;
        hub.MessageSent += OnMessage;
        if (session == null) hub.StateChanged += OnChanged;
        using var listener = session != null ? hub.OpenListener(session) : null;
        try
        {
            await ctx.Response.WriteAsync($"event: ready\ndata: {{\"lastId\":{lastSent}}}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);

            if (session != null && since is not null)
            {
                foreach (var m in hub.GetDeliverable(session, since.Value))
                {
                    await WriteMessageAsync(ctx, m, ct);
                    lastSent = m.Id;
                }
            }

            while (!ct.IsCancellationRequested)
            {
                using var beat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                beat.CancelAfter(Heartbeat);
                Message? item;
                try
                {
                    item = await queue.Reader.ReadAsync(beat.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await ctx.Response.WriteAsync(": heartbeat\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    continue;
                }

                if (item == null)
                {
                    Interlocked.Exchange(ref changedPending, 0);
                    await ctx.Response.WriteAsync("event: changed\ndata: {}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
                else if (item.Id > lastSent)
                {
                    await WriteMessageAsync(ctx, item, ct);
                    lastSent = item.Id;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { } // client went away mid-write
        finally
        {
            hub.MessageSent -= OnMessage;
            hub.StateChanged -= OnChanged;
        }
    }

    private static async Task WriteMessageAsync(HttpContext ctx, Message m, CancellationToken ct)
    {
        await ctx.Response.WriteAsync($"id: {m.Id}\nevent: message\ndata: {JsonSerializer.Serialize(m)}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
}
