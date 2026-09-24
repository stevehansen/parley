using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Parley.Mcp;
using Parley.Sharing;
using Parley.Update;

namespace Parley.Hub;

/// <summary>
/// The hub process: one <see cref="CollabHub"/> behind an HTTP API on loopback and, while shared,
/// on the overlay-network addresses of <see cref="HubEndpoints"/> too.
///
///   POST /mcp                      MCP Streamable HTTP (see <see cref="HttpMcpEndpoint"/>)
///   GET  /api/health               liveness probe used by the shim before auto-starting a hub
///   GET  /api/topics | /api/sessions | /api/messages?topic=&amp;count=
///   POST /api/messages             {session, topic, content} — send without MCP (UIs, scripts)
///   DELETE /api/topics/{name}      delete a topic and its messages
///   POST /api/update               install the newest release (restarts the hub)
///   POST /api/shutdown             stop, saving state first (how `parley update` stops the hub)
///   GET  /api/devices              paired devices and pending pairing codes
///   POST /api/devices              {name} — a pairing code for a new device, and the URLs to join
///   DELETE /api/devices/{name}     unpair a device
///   POST /api/pair                 {code, cookie?} — trade a pairing code for a device token (in
///                                  the body, or with cookie: true as the web UI's cookie)
///   POST /api/unpair               a paired device removes itself (`parley leave`)
///   GET  /                         the web UI
///   GET  /api/events               SSE. With ?session=X: the messages X should receive (its topics,
///                                  not its own), optionally replaying ids after ?since=N; the
///                                  stream also counts as X being connected. Without: every message
///                                  plus coalesced "changed" signals for dashboards.
///
/// Loopback connections are trusted, as any local process is, but only with a loopback Host
/// header, so a DNS-rebinding page can't reach the hub. Any other connection must come from an
/// allowed network and carry a device token (bearer, or the web UI's cookie); only the page itself
/// and /api/pair are served without one, and shutdown, update and device management stay local.
/// Every state-changing endpoint takes a JSON body, which a cross-site page can't send without a
/// CORS preflight the hub never approves, and the cookie is SameSite=Strict — no web page can post
/// into agents' conversations.
/// </summary>
public static class HubServer
{
    internal static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(25);

    /// <summary>The web UI's credential: the device token, HttpOnly (EventSource can't send headers).</summary>
    internal const string TokenCookie = "parley_token";

    /// <summary>The device sessions on the hub machine itself count as.</summary>
    internal static readonly string LocalDevice = Environment.MachineName.ToLowerInvariant();

    private const string DeviceItem = "parley.device";

    // Only from the hub machine: stopping or updating the hub, and deciding who may use it.
    private static readonly string[] LocalOnlyPaths = ["/api/shutdown", "/api/update", "/api/devices"];

    /// <param name="devices">Who may connect from other machines; null shares with nobody.</param>
    public static WebApplication Build(CollabHub hub, int port, UpdateChecker? updates = null, DeviceRegistry? devices = null)
    {
        devices ??= new DeviceRegistry();
        var endpoints = new HubEndpoints(port, devices);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(o => o.Configure(endpoints.Configuration, reloadOnChange: true));
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.PropertyNameCaseInsensitive = true;
        });

        var app = builder.Build();
        var mcp = new HttpMcpEndpoint(hub);

        app.Use(async (ctx, next) =>
        {
            var remote = ctx.Connection.RemoteIpAddress;
            if (remote == null || IPAddress.IsLoopback(remote))
            {
                if (!IsLoopbackHost(ctx.Request.Host.Host))
                {
                    ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                    return;
                }
                await next();
                return;
            }

            if (!devices.IsAllowed(remote))
            {
                await Refuse(ctx, StatusCodes.Status403Forbidden, $"{remote} is outside the networks this hub is shared with");
                return;
            }
            var path = ctx.Request.Path.Value ?? "";
            if (LocalOnlyPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                await Refuse(ctx, StatusCodes.Status403Forbidden, "Only available on the hub machine itself");
                return;
            }
            if (path == "/" || path.Equals("/api/pair", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            var device = devices.Authenticate(BearerToken(ctx) ?? ctx.Request.Cookies[TokenCookie]);
            if (device == null)
            {
                await Refuse(ctx, StatusCodes.Status401Unauthorized,
                    "This device isn't paired with the hub, or was removed. Pair it again: `parley devices add <name>` on the hub machine.");
                return;
            }
            device.Touch(ctx.Request.Headers[ParleyConfig.VersionHeader].FirstOrDefault());
            ctx.Items[DeviceItem] = device;
            // Unpairing also ends what's in flight: event streams and read_messages long-polls.
            using var _ = device.Revoked.Register(ctx.Abort);
            await next();
        });

        app.MapGet("/", () => Results.Content(WebUi.Html, "text/html; charset=utf-8"));
        app.MapGet("/api/health", (HttpContext ctx) => Results.Ok(new
        {
            name = "parley",
            version = Protocol.Version,
            apiVersion = Protocol.ApiVersion,
            device = DeviceOf(ctx),
            // The web UI offers hub-only actions (update, adding devices) only where they work.
            local = ctx.Items[DeviceItem] == null,
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

        app.MapGet("/api/devices", () => Results.Ok(devices.List()));
        app.MapPost("/api/devices", (AddDeviceRequest req) =>
        {
            string code;
            try
            {
                code = devices.CreatePairingCode(req.Name ?? "");
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            var urls = endpoints.NetworkAddresses.Select(a => $"http://{HubEndpoints.Format(a)}:{port}").ToList();
            // The page pairs on its own when opened through this link, so scanning the QR is all a phone needs.
            var link = urls.Count > 0 ? $"{urls[0]}/#/pair/{code}" : null;
            return Results.Ok(new
            {
                code,
                expiresAt = DateTime.UtcNow + DeviceRegistry.CodeLifetime,
                urls,
                link,
                qr = link != null ? QrDataUrl(link) : null,
                allowFrom = devices.AllowFrom.Select(n => n.ToString()),
            });
        });
        app.MapDelete("/api/devices/{name}", (string name) =>
            devices.Remove(name) ? Results.NoContent() : Results.NotFound());

        app.MapPost("/api/pair", (HttpContext ctx, PairRequest req) =>
        {
            if (devices.Redeem(req.Code ?? "") is not { } paired)
                return Results.Json(new { error = "Unknown or expired pairing code" }, statusCode: StatusCodes.Status403Forbidden);
            var (device, token) = paired;
            if (req.Cookie != true) return Results.Ok(new { device = device.Name, hub = LocalDevice, token });
            ctx.Response.Cookies.Append(TokenCookie, token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = ctx.Request.IsHttps,
                MaxAge = TimeSpan.FromDays(3650),
            });
            return Results.Ok(new { device = device.Name, hub = LocalDevice });
        });
        app.MapPost("/api/unpair", (HttpContext ctx, UnpairRequest _) =>
        {
            if (ctx.Items[DeviceItem] is not Device device)
                return Results.BadRequest(new { error = "Only a paired device can unpair itself" });
            ctx.Response.Cookies.Delete(TokenCookie);
            devices.Remove(device.Name);
            return Results.NoContent();
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
                DeviceOf(ctx),
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
            StreamEventsAsync(ctx, hub, devices, string.IsNullOrWhiteSpace(session) ? null : session.Trim(), since,
                app.Lifetime.ApplicationStopping));

        app.Lifetime.ApplicationStopping.Register(hub.Dispose);
        app.Lifetime.ApplicationStopped.Register(endpoints.Dispose);
        return app;
    }

    /// <summary>The device a request came from: its paired name, or the hub machine's own.</summary>
    private static string DeviceOf(HttpContext ctx) => (ctx.Items[DeviceItem] as Device)?.Name ?? LocalDevice;

    /// <summary>A QR code for <paramref name="text"/> as an SVG data URL (dark on white, which every scanner reads).</summary>
    private static string QrDataUrl(string text)
    {
        using var data = QRCoder.QRCodeGenerator.GenerateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var svg = new QRCoder.SvgQRCode(data).GetGraphic(8);
        return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }

    private static string? BearerToken(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.FirstOrDefault();
        return auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
    }

    private static Task Refuse(HttpContext ctx, int status, string error)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { error });
    }

    public sealed record SendRequest(string? Session, string? Topic, string? Content);

    public sealed record AddDeviceRequest(string? Name);

    public sealed record PairRequest(string? Code, bool? Cookie);

    public sealed record UnpairRequest;

    public sealed record UpdateRequest;

    public sealed record ShutdownRequest;

    private static bool IsLoopbackHost(string host) =>
        host is "localhost" or "127.0.0.1" or "[::1]" or "::1";

    private static async Task StreamEventsAsync(HttpContext ctx, CollabHub hub, DeviceRegistry devices, string? session, int? since,
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
        if (session == null)
        {
            hub.StateChanged += OnChanged;
            devices.SharingChanged += OnChanged; // a device paired, was removed, or its code lapsed
        }
        using var listener = session != null ? hub.OpenListener(session, DeviceOf(ctx)) : null;
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
            devices.SharingChanged -= OnChanged;
        }
    }

    private static async Task WriteMessageAsync(HttpContext ctx, Message m, CancellationToken ct)
    {
        await ctx.Response.WriteAsync($"id: {m.Id}\nevent: message\ndata: {JsonSerializer.Serialize(m)}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
}
