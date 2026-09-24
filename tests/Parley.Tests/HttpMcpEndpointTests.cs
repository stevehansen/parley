using System.Text.Json;
using Parley.Hub;
using Parley.Mcp;

namespace Parley.Tests;

public class HttpMcpEndpointTests
{
    private readonly CollabHub _hub = new();
    private readonly HttpMcpEndpoint _mcp;

    public HttpMcpEndpointTests() => _mcp = new HttpMcpEndpoint(_hub);

    private static string Rpc(string method, object? @params = null, int id = 1) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params });

    private static string CallTool(string name, object args) => Rpc("tools/call", new { name, arguments = args });

    private static JsonElement Parse(HttpMcpEndpoint.Result r) => JsonDocument.Parse(r.Body!).RootElement;

    private static string ToolText(HttpMcpEndpoint.Result r) =>
        Parse(r).GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    [Fact]
    public async Task XSessionHeader_IdentifiesCaller_WithoutHandshake()
    {
        var r = await _mcp.HandleAsync(CallTool("send_message", new { topic = "work", content = "hi" }), "backend", null, null, null, default);

        r.StatusCode.ShouldBe(200);
        ToolText(r).ShouldStartWith("Message #1 sent");
        _hub.GetRecentMessages().ShouldHaveSingleItem().Sender.ShouldBe("backend");
    }

    [Fact]
    public async Task Initialize_WithRoots_NamesSessionAfterFolder_AndIssuesSessionId()
    {
        var init = await _mcp.HandleAsync(Rpc("initialize", new { protocolVersion = "2025-06-18", roots = new[] { new { uri = "file:///P:/Frontend" } } }), null, null, null, null, default);

        init.McpSessionId.ShouldNotBeNull();
        Parse(init).GetProperty("result").GetProperty("protocolVersion").GetString().ShouldBe("2025-06-18");

        await _mcp.HandleAsync(CallTool("subscribe", new { topic = "work" }), null, init.McpSessionId, null, null, default);
        _hub.GetTopics().Single().Subscribers.ShouldBe(["Frontend"]);
    }

    [Fact]
    public async Task Initialize_NeverAgreesToStatelessRevision()
    {
        var init = await _mcp.HandleAsync(Rpc("initialize", new { protocolVersion = "2026-07-28" }), null, null, null, null, default);
        Parse(init).GetProperty("result").GetProperty("protocolVersion").GetString().ShouldBe("2025-11-25");
    }

    [Fact]
    public async Task AnonymousSession_GetsSetSessionName_AndRenameKeepsSubscriptions()
    {
        var init = await _mcp.HandleAsync(Rpc("initialize", new { }), null, null, null, null, default);
        var id = init.McpSessionId;

        var list = await _mcp.HandleAsync(Rpc("tools/list"), null, id, null, null, default);
        Parse(list).GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ShouldContain("set_session_name");

        await _mcp.HandleAsync(CallTool("subscribe", new { topic = "work" }), null, id, null, null, default);
        await _mcp.HandleAsync(CallTool("set_session_name", new { name = "api", working_dir = "/src/api" }), null, id, null, null, default);
        await _mcp.HandleAsync(CallTool("send_message", new { topic = "work", content = "renamed" }), null, id, null, null, default);

        _hub.GetTopics().Single().Subscribers.ShouldBe(["api"]);
        _hub.GetRecentMessages().Single().Sender.ShouldBe("api");

        var after = await _mcp.HandleAsync(Rpc("tools/list"), null, id, null, null, default);
        Parse(after).GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ShouldNotContain("set_session_name");
    }

    [Fact]
    public async Task UnknownSessionId_Returns404_SoClientReinitializes()
    {
        var r = await _mcp.HandleAsync(Rpc("tools/list"), null, "gone", null, null, default);
        r.StatusCode.ShouldBe(404);
    }

    [Fact]
    public async Task NoIdentity_Returns400()
    {
        var r = await _mcp.HandleAsync(Rpc("tools/list"), null, null, null, null, default);
        r.StatusCode.ShouldBe(400);
    }

    [Fact]
    public async Task Notification_Returns202_WithoutBody()
    {
        var r = await _mcp.HandleAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }), "backend", null, null, null, default);
        r.StatusCode.ShouldBe(202);
        r.Body.ShouldBeNull();
    }

    [Fact]
    public async Task ToolResults_CarryUnreadHints()
    {
        await _mcp.HandleAsync(CallTool("subscribe", new { topic = "work" }), "frontend", null, null, null, default);
        await _mcp.HandleAsync(CallTool("send_message", new { topic = "work", content = "hi" }), "backend", null, null, null, default);

        var r = await _mcp.HandleAsync(CallTool("list_topics", new { }), "frontend", null, null, null, default);
        ToolText(r).ShouldContain("[You have 1 unread message(s) on topic 'work']");
    }

    [Theory]
    [InlineData("file:///P:/TerminalHost", "TerminalHost")]
    [InlineData("file:///home/me/backend/", "backend")]
    public void RootDirectory_ToFolderName(string uri, string expected)
    {
        var p = JsonDocument.Parse(JsonSerializer.Serialize(new { roots = new[] { new { uri } } })).RootElement;
        HttpMcpEndpoint.FolderName(HttpMcpEndpoint.RootDirectory(p)!).ShouldBe(expected);
    }
}
