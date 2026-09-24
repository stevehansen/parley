using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Parley.Hub;
using Parley.Sharing;
using Parley.Shim;

namespace Parley.Tests;

/// <summary>
/// A shared hub reached over one of this machine's real non-loopback addresses, standing in for
/// the overlay network. Without such an address (a sandbox with loopback only) the tests pass vacuously.
/// </summary>
public class SharingTests : IAsyncLifetime
{
    private readonly CollabHub _hub = new();
    private readonly IPAddress? _address = NonLoopbackAddress();
    private DeviceRegistry _devices = null!;
    private WebApplication _app = null!;
    private int _port;
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };

    private string Local => $"http://127.0.0.1:{_port}";
    private string Remote => $"http://{_address}:{_port}";

    public async Task InitializeAsync()
    {
        _port = FreePort();
        _devices = new DeviceRegistry(allowFrom: _address != null ? [$"{_address}/32"] : ["192.0.2.0/24"]);
        _app = HubServer.Build(_hub, _port, devices: _devices);
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _devices.Dispose();
    }

    [Fact]
    public async Task NotShared_ListensOnLoopbackOnly()
    {
        if (_address == null) return;
        (await _http.GetAsync($"{Local}/api/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Should.ThrowAsync<HttpRequestException>(() => _http.GetAsync($"{Remote}/api/health"));
    }

    [Fact]
    public async Task PairedDevice_UsesTheHub_AndUnpairingClosesTheNetworkSide()
    {
        if (_address == null) return;
        var code = await AddDeviceAsync("laptop");

        // Before pairing: only the page and the pairing endpoint answer.
        (await _http.GetAsync($"{Remote}/api/topics")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _http.GetAsync($"{Remote}/")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _http.PostAsJsonAsync($"{Remote}/api/pair", new { code = "AAA-AAA-AAA" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var token = await PairAsync(code);
        (await SendAsync(HttpMethod.Get, $"{Remote}/api/topics", token)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Tool calls from the device are attributed to it.
        var mcp = new HttpRequestMessage(HttpMethod.Post, $"{Remote}/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = "send_message", arguments = new { topic = "work", content = "from the laptop" } },
            }), Encoding.UTF8, "application/json"),
        };
        mcp.Headers.Add("X-Session", "api");
        mcp.Headers.Authorization = new("Bearer", token);
        (await _http.SendAsync(mcp)).StatusCode.ShouldBe(HttpStatusCode.OK);
        _hub.GetSessions().Single(s => s.Name == "api").Device.ShouldBe("laptop");

        (await _http.DeleteAsync($"{Local}/api/devices/laptop")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await WaitUntilAsync(async () =>
        {
            try { await _http.GetAsync($"{Remote}/api/health"); return false; }
            catch (HttpRequestException) { return true; }
        });
        (await _http.GetAsync($"{Local}/api/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HubOnlyEndpoints_RefuseRemoteCallers_EvenPaired()
    {
        if (_address == null) return;
        var token = await PairAsync(await AddDeviceAsync("laptop"));
        await AddDeviceAsync("keeps-it-shared");

        foreach (var (method, path) in new[] { (HttpMethod.Get, "/api/devices"), (HttpMethod.Post, "/api/devices"), (HttpMethod.Post, "/api/shutdown"), (HttpMethod.Post, "/api/update") })
            (await SendAsync(method, Remote + path, token, "{}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{method} {path}");
    }

    [Fact]
    public async Task Unpairing_AbortsTheDevicesOpenEventStream()
    {
        if (_address == null) return;
        var token = await PairAsync(await AddDeviceAsync("laptop"));
        await AddDeviceAsync("keeps-it-shared");

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Remote}/api/events?session=api");
        req.Headers.Authorization = new("Bearer", token);
        using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stream = await response.Content.ReadAsStreamAsync();
        _hub.GetSessions().Single(s => s.Name == "api").Device.ShouldBe("laptop");

        _devices.Remove("laptop");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Should.ThrowAsync<Exception>(async () =>
        {
            var buffer = new byte[1024];
            while (await stream.ReadAsync(buffer, cts.Token) > 0) { }
            throw new IOException("stream ended"); // a clean end counts too
        });
        cts.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task Browser_PairsWithAnHttpOnlyStrictCookie()
    {
        if (_address == null) return;
        var code = await AddDeviceAsync("phone");
        var r = await _http.PostAsJsonAsync($"{Remote}/api/pair", new { code, cookie = true });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await r.Content.ReadAsStringAsync()).ShouldNotContain("token");
        var cookie = r.Headers.GetValues("Set-Cookie").Single();
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("samesite=strict", Case.Insensitive);

        var req = new HttpRequestMessage(HttpMethod.Get, $"{Remote}/api/topics");
        req.Headers.Add("Cookie", cookie.Split(';')[0]);
        (await _http.SendAsync(req)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DashboardStream_SignalsDeviceChanges()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await _http.GetAsync($"{Local}/api/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var events = SseReader.ReadAsync(await response.Content.ReadAsStreamAsync(cts.Token), TimeSpan.FromMinutes(1), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        (await events.MoveNextAsync()).ShouldBeTrue();
        events.Current.evt.ShouldBe("ready");

        _devices.CreatePairingCode("phone"); // what the web UI's device list needs to hear about
        (await events.MoveNextAsync()).ShouldBeTrue();
        events.Current.evt.ShouldBe("changed");
    }

    [Fact]
    public async Task Loopback_StillRequiresALoopbackHost()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{Local}/api/topics");
        req.Headers.Host = "rebind.example.com";
        (await _http.SendAsync(req)).StatusCode.ShouldBe(HttpStatusCode.MisdirectedRequest);
    }

    private async Task<string> AddDeviceAsync(string name)
    {
        var r = await _http.PostAsJsonAsync($"{Local}/api/devices", new { name });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("urls").EnumerateArray().Select(u => u.GetString()).ShouldContain(Remote);
        body.GetProperty("link").GetString().ShouldBe($"{Remote}/#/pair/{body.GetProperty("code").GetString()}");
        body.GetProperty("qr").GetString().ShouldStartWith("data:image/svg+xml;base64,");
        await WaitUntilAsync(async () =>
        {
            try { await _http.GetAsync($"{Remote}/"); return true; }
            catch (HttpRequestException) { return false; }
        });
        return body.GetProperty("code").GetString()!;
    }

    private async Task<string> PairAsync(string code)
    {
        var r = await _http.PostAsJsonAsync($"{Remote}/api/pair", new { code });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, string? json = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new("Bearer", token);
        if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return _http.SendAsync(req);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 50; i++)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("condition not met within 5 s");
    }

    private static IPAddress? NonLoopbackAddress() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
