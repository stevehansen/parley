using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Parley.Hub;
using Parley.Mcp;

namespace Parley.Shim;

/// <summary>
/// <c>parley mcp</c>: the per-session MCP server an AI client spawns over stdio.
///
/// It gives the session a stable identity (PARLEY_SESSION, else the working directory's folder
/// name) and forwards tool calls to the hub under that name, starting a hub in the background when
/// none answers. Alongside, it holds the hub's event stream for this session open and turns every
/// incoming message into a Claude Code channel notification, which wakes an idle session — the
/// whole point of running over stdio: channels are only honoured for stdio servers.
///
/// Pushed messages do not move the read cursor. If the client was started without channels
/// enabled the notification is silently dropped, and the unread hints are then the only way the
/// agent learns about the message.
/// </summary>
public sealed class McpShim
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);

    /// <summary>Two missed hub heartbeats and a margin: the stream is dead, reconnect.</summary>
    private static readonly TimeSpan StreamIdleTimeout = HubServer.Heartbeat * 2.5;

    private readonly string _session;
    private readonly string _workingDir;
    private readonly string _hubUrl;
    private readonly HttpClient _http;
    private readonly Action<string> _write;
    private readonly SemaphoreSlim _hubStart = new(1, 1);
    private int _lastPushedId = -1;
    private DateTime _hubStartedAt = DateTime.MinValue;

    /// <param name="token">This device's token for a hub on another machine; null for a local hub.</param>
    public McpShim(string session, string workingDir, string hubUrl, Action<string> write, string? token = null)
    {
        _http = ParleyConfig.CreateHubClient(Timeout.InfiniteTimeSpan, token);
        _session = session;
        _workingDir = workingDir;
        _hubUrl = hubUrl;
        _write = write;
    }

    public static string DefaultSessionName(string workingDir) =>
        Environment.GetEnvironmentVariable("PARLEY_SESSION") is { Length: > 0 } s ? s : HttpMcpEndpoint.FolderName(workingDir);

    public static async Task<int> RunStdioAsync(CancellationToken ct)
    {
        var writeLock = new object();
        var stdout = Console.OpenStandardOutput();
        void Write(string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            lock (writeLock)
            {
                stdout.Write(bytes);
                stdout.Flush();
            }
        }

        var cwd = Environment.CurrentDirectory;
        var shim = new McpShim(DefaultSessionName(cwd), cwd, ParleyConfig.HubUrl, Write, ParleyConfig.HubToken);
        Log.Info($"mcp shim for session '{shim._session}' ({cwd}) → {shim._hubUrl}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var push = shim.PushLoopAsync(cts.Token);

        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        var inflight = new List<Task>();
        while (!cts.IsCancellationRequested)
        {
            string? line;
            try { line = await reader.ReadLineAsync(cts.Token); }
            catch (OperationCanceledException) { break; }
            if (line == null) break; // client closed stdin: shut down
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Concurrently: a read_messages long-poll must not hold up pings or other tool calls.
            inflight.RemoveAll(t => t.IsCompleted);
            inflight.Add(Task.Run(async () =>
            {
                var response = await shim.HandleLineAsync(line, cts.Token);
                if (response != null) Write(response);
            }, cts.Token));
        }

        cts.Cancel();
        try { await Task.WhenAll(inflight.Append(push)); } catch (OperationCanceledException) { }
        return 0;
    }

    /// <summary>Handles one JSON-RPC line from the client; returns the response line, if any.</summary>
    internal async Task<string?> HandleLineAsync(string line, CancellationToken ct)
    {
        JsonRpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<JsonRpcRequest>(line, Json.Options);
        }
        catch (JsonException)
        {
            return Json.Serialize(JsonRpcResponse.Fail(null, -32700, "Parse error"));
        }

        // Responses to server requests (we send none) and notifications need no answer.
        if (request?.Method is null || request.IsNotification) return null;

        try
        {
            return request.Method switch
            {
                "initialize" => Json.Serialize(JsonRpcResponse.Success(request.Id, new
                {
                    protocolVersion = Protocol.Negotiate(request.Params),
                    capabilities = new Dictionary<string, object>
                    {
                        ["tools"] = new { listChanged = false },
                        ["experimental"] = new Dictionary<string, object> { ["claude/channel"] = new { } },
                    },
                    serverInfo = new { name = "parley", version = Protocol.Version },
                    instructions = AgentInstructions.WithPush,
                })),
                "ping" => Json.Serialize(JsonRpcResponse.Success(request.Id, new { })),
                "tools/list" or "tools/call" => await ForwardAsync(request, line, ct),
                _ => Json.Serialize(JsonRpcResponse.Fail(request.Id, -32601, $"Method not found: {request.Method}")),
            };
        }
        catch (HttpRequestException ex)
        {
            return Json.Serialize(JsonRpcResponse.Fail(request.Id, -32603, $"Parley hub unreachable at {_hubUrl}: {ex.Message}"));
        }
    }

    private async Task<string> ForwardAsync(JsonRpcRequest request, string line, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await PostAsync(line, ct);
        }
        catch (HttpRequestException)
        {
            await EnsureHubAsync(ct);
            response = await PostAsync(line, ct);
        }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            // A shared hub refusing this device (unpaired, wrong network) answers in plain JSON, not JSON-RPC.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Json.Serialize(JsonRpcResponse.Fail(request.Id, -32603,
                    $"The Parley hub at {_hubUrl} refused this device ({(int)response.StatusCode}): {ErrorOf(body)}"));
            return body;
        }
    }

    private Task<HttpResponseMessage> PostAsync(string line, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_hubUrl}/mcp")
        {
            Content = new StringContent(line, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Session", _session);
        req.Headers.Add("X-Working-Dir", _workingDir);
        return _http.SendAsync(req, ct);
    }

    /// <summary>Holds the session's event stream open for the life of the process, reconnecting as needed.</summary>
    private async Task PushLoopAsync(CancellationToken ct)
    {
        var loggedDown = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EnsureHubAsync(ct);
                var url = $"{_hubUrl}/api/events?session={Uri.EscapeDataString(_session)}"
                          + (_lastPushedId >= 0 ? $"&since={_lastPushedId}" : "");
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                req.Headers.Add("X-Working-Dir", _workingDir);

                using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                loggedDown = false;
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await foreach (var (evt, data) in SseReader.ReadAsync(stream, StreamIdleTimeout, ct))
                    OnEvent(evt, data);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException)
            {
                if (!loggedDown) Log.Error("event stream lost; reconnecting", ex);
                loggedDown = true;
            }

            try { await Task.Delay(ReconnectDelay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    internal void OnEvent(string evt, string data)
    {
        switch (evt)
        {
            case "ready":
                using (var doc = JsonDocument.Parse(data))
                    if (_lastPushedId < 0 && doc.RootElement.TryGetProperty("lastId", out var id))
                        _lastPushedId = id.GetInt32();
                break;

            case "message":
                var m = JsonSerializer.Deserialize<Message>(data);
                if (m == null || m.Id <= _lastPushedId) return;
                _lastPushedId = m.Id;
                _write(Json.Serialize(new
                {
                    jsonrpc = "2.0",
                    method = "notifications/claude/channel",
                    @params = new
                    {
                        content = m.Content,
                        // Meta keys must be identifier-like or Claude Code drops them.
                        meta = new Dictionary<string, string>
                        {
                            ["topic"] = m.Topic,
                            ["sender"] = m.Sender,
                            ["message_id"] = m.Id.ToString(),
                        },
                    },
                }));
                break;
        }
    }

    /// <summary>Starts a background hub unless one already answers; waits briefly for it to come up.</summary>
    private async Task EnsureHubAsync(CancellationToken ct)
    {
        if (await IsHubUpAsync(ct)) return;
        await _hubStart.WaitAsync(ct);
        try
        {
            // No second probe before starting: a refused loopback connect costs ~2s on Windows.
            // Whoever queued behind a fresh start just waits for that hub instead.
            if (DateTime.UtcNow - _hubStartedAt > TimeSpan.FromSeconds(10))
            {
                if (!IsLocal(_hubUrl)) return; // a remote hub is not ours to start
                Log.Info("no hub running; starting one");
                _hubStartedAt = DateTime.UtcNow;
                try
                {
                    SelfProcess.StartDetached("serve --background");
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    // Mid-update the tool's files can be briefly missing; the updater starts the hub.
                    Log.Error("could not start a hub", ex);
                    return;
                }
            }
            for (var i = 0; i < 50 && !await IsHubUpAsync(ct); i++)
                await Task.Delay(100, ct);
        }
        finally
        {
            _hubStart.Release();
        }
    }

    private async Task<bool> IsHubUpAsync(CancellationToken ct)
    {
        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromSeconds(2));
            using var r = await _http.GetAsync($"{_hubUrl}/api/health", probe.Token);
            return r.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static string ErrorOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? body : body;
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static bool IsLocal(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host == "localhost");
}
