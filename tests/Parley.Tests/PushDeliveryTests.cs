using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Parley.Hub;
using Parley.Shim;

namespace Parley.Tests;

/// <summary>End to end over a real hub: event stream filtering, replay, and the shim's channel notifications.</summary>
public class PushDeliveryTests : IAsyncLifetime
{
    private readonly CollabHub _hub = new();
    private WebApplication _app = null!;
    private string _url = "";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task InitializeAsync()
    {
        var port = FreePort();
        _url = $"http://127.0.0.1:{port}";
        _app = HubServer.Build(_hub, port);
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task SessionStream_DeliversOthersMessages_OnSubscribedTopicsOnly()
    {
        _hub.Subscribe("frontend", "api");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = await OpenStreamAsync("frontend", null, cts.Token);
        await events.WaitForAsync(e => e.evt == "ready", cts.Token);
        _hub.GetSessions().Single(s => s.Name == "frontend").Listeners.ShouldBe(1);

        _hub.SendMessage("frontend", "api", "own message");
        _hub.SendMessage("backend", "elsewhere", "not subscribed");
        _hub.SendMessage("backend", "api", "UserDTO changed");

        var msg = await events.WaitForAsync(e => e.evt == "message", cts.Token);
        JsonDocument.Parse(msg.data).RootElement.GetProperty("content").GetString().ShouldBe("UserDTO changed");
        events.Count(e => e.evt == "message").ShouldBe(1);
    }

    [Fact]
    public async Task SessionStream_WithSince_ReplaysMissedMessages()
    {
        _hub.Subscribe("frontend", "api");
        _hub.SendMessage("backend", "api", "one");
        _hub.SendMessage("backend", "api", "two");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = await OpenStreamAsync("frontend", 1, cts.Token);
        var msg = await events.WaitForAsync(e => e.evt == "message", cts.Token);
        JsonDocument.Parse(msg.data).RootElement.GetProperty("content").GetString().ShouldBe("two");
    }

    [Fact]
    public async Task RestSend_ReachesStream()
    {
        _hub.Subscribe("frontend", "api");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = await OpenStreamAsync("frontend", null, cts.Token);
        await events.WaitForAsync(e => e.evt == "ready", cts.Token);

        var r = await _http.PostAsJsonAsync($"{_url}/api/messages", new { session = "ui", topic = "api", content = "from the UI" }, cts.Token);
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        await events.WaitForAsync(e => e.evt == "message" && e.data.Contains("from the UI"), cts.Token);
    }

    [Fact]
    public async Task Shim_ForwardsToolsUnderItsName_AndPushesChannelNotifications()
    {
        var written = new BlockingCollection<string>();
        var shim = new McpShim("frontend", "/src/frontend", _url, written.Add);

        var init = await shim.HandleLineAsync(Rpc("initialize", new { protocolVersion = "2025-06-18" }), default);
        var caps = JsonDocument.Parse(init!).RootElement.GetProperty("result").GetProperty("capabilities");
        caps.GetProperty("experimental").TryGetProperty("claude/channel", out _).ShouldBeTrue();

        await shim.HandleLineAsync(Rpc("tools/call", new { name = "subscribe", arguments = new { topic = "api" } }), default);
        _hub.GetTopics().Single().Subscribers.ShouldBe(["frontend"]);
        _hub.GetSessions().Single(s => s.Name == "frontend").WorkingDir.ShouldBe("/src/frontend");

        shim.OnEvent("ready", "{\"lastId\":0}");
        var msg = _hub.SendMessage("backend", "api", "UserDTO changed");
        shim.OnEvent("message", JsonSerializer.Serialize(msg));
        shim.OnEvent("message", JsonSerializer.Serialize(msg)); // replayed duplicate is dropped

        var note = JsonDocument.Parse(written.Take()).RootElement;
        note.GetProperty("method").GetString().ShouldBe("notifications/claude/channel");
        note.GetProperty("params").GetProperty("content").GetString().ShouldBe("UserDTO changed");
        var meta = note.GetProperty("params").GetProperty("meta");
        meta.GetProperty("topic").GetString().ShouldBe("api");
        meta.GetProperty("sender").GetString().ShouldBe("backend");
        meta.GetProperty("message_id").GetString().ShouldBe(msg.Id.ToString());
        written.Count.ShouldBe(0);
    }

    [Fact]
    public async Task WebUi_IsServedAtRoot()
    {
        var html = await _http.GetStringAsync(_url + "/");
        html.ShouldContain("<title>Parley</title>");
    }

    [Fact]
    public async Task ForeignHostHeader_IsRejected()
    {
        // A DNS-rebinding page reaches 127.0.0.1 under its own host name.
        var req = new HttpRequestMessage(HttpMethod.Get, _url + "/api/topics");
        req.Headers.Host = "evil.example";
        (await _http.SendAsync(req)).StatusCode.ShouldBe(HttpStatusCode.MisdirectedRequest);
    }

    [Fact]
    public async Task CrossSiteStylePosts_AreRefused()
    {
        // What an HTML form or no-cors fetch can send: no JSON content type.
        var send = await _http.PostAsync(_url + "/api/messages",
            new StringContent("{\"session\":\"x\",\"topic\":\"t\",\"content\":\"c\"}", System.Text.Encoding.UTF8, "text/plain"));
        send.IsSuccessStatusCode.ShouldBeFalse();
        var mcp = await _http.PostAsync(_url + "/mcp",
            new StringContent(Rpc("initialize", new { }), System.Text.Encoding.UTF8, "text/plain"));
        mcp.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        _hub.GetRecentMessages().ShouldBeEmpty();
    }

    [Fact]
    public async Task Shutdown_StopsTheHub_OnlyForJsonPosts()
    {
        var formPost = await _http.PostAsync(_url + "/api/shutdown", new StringContent("", System.Text.Encoding.UTF8, "text/plain"));
        formPost.IsSuccessStatusCode.ShouldBeFalse();
        _app.Lifetime.ApplicationStopping.IsCancellationRequested.ShouldBeFalse();

        var ok = await _http.PostAsync(_url + "/api/shutdown", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        ok.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await Task.Delay(200);
        _app.Lifetime.ApplicationStopping.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteTopic_Endpoint()
    {
        _hub.SendMessage("a", "gone", "x");
        (await _http.DeleteAsync(_url + "/api/topics/gone")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _hub.GetTopics().ShouldBeEmpty();
    }

    [Fact]
    public async Task Shim_IgnoresNotifications_AndAnswersPing()
    {
        var shim = new McpShim("frontend", "/src/frontend", _url, _ => { });
        (await shim.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", default)).ShouldBeNull();
        (await shim.HandleLineAsync(Rpc("ping", null), default))!.ShouldContain("\"result\":{}");
    }

    private static string Rpc(string method, object? @params) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params });

    private async Task<EventLog> OpenStreamAsync(string session, int? since, CancellationToken ct)
    {
        var url = $"{_url}/api/events?session={session}" + (since is { } s ? $"&since={s}" : "");
        var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var log = new EventLog();
        var stream = await response.Content.ReadAsStreamAsync(ct);
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in SseReader.ReadAsync(stream, ct)) log.Add(e);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }, ct);
        return log;
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private sealed class EventLog
    {
        private readonly List<(string evt, string data)> _events = new();
        private readonly SemaphoreSlim _signal = new(0);

        public void Add((string evt, string data) e)
        {
            lock (_events) _events.Add(e);
            _signal.Release();
        }

        public int Count(Func<(string evt, string data), bool> match)
        {
            lock (_events) return _events.Count(match);
        }

        public async Task<(string evt, string data)> WaitForAsync(Func<(string evt, string data), bool> match, CancellationToken ct)
        {
            while (true)
            {
                lock (_events)
                {
                    var hit = _events.FirstOrDefault(match);
                    if (hit != default) return hit;
                }
                await _signal.WaitAsync(ct);
            }
        }
    }
}
