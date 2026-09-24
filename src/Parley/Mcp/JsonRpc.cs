using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Parley.Mcp;

public class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }

    [JsonIgnore]
    public bool IsNotification => Id is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };
}

public class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("result")]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }

    public static JsonRpcResponse Success(JsonElement? id, object result) => new() { Id = id, Result = result };

    public static JsonRpcResponse Fail(JsonElement? id, int code, string message) =>
        new() { Id = id, Error = new JsonRpcError { Code = code, Message = message } };
}

public class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public class ToolDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("inputSchema")]
    public JsonObject InputSchema { get; set; } = new();
}

public class CallToolResult
{
    [JsonPropertyName("content")]
    public List<TextContent> Content { get; set; } = new();

    [JsonPropertyName("isError")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsError { get; set; }

    public static CallToolResult Text(string text) => new() { Content = [new TextContent { Text = text }] };

    public static CallToolResult Error(string text) => new() { Content = [new TextContent { Text = text }], IsError = true };
}

public class TextContent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>MCP protocol-version negotiation shared by the HTTP endpoint and the stdio shim.</summary>
internal static class Protocol
{
    /// <summary>
    /// Handshake-era revisions only. 2026-07-28 drops the initialize handshake and sessions, and a
    /// Claude Code channel server that negotiates it is not registered as a channel — so the shim
    /// must never agree to it, or push delivery silently stops.
    /// </summary>
    private static readonly string[] Supported = ["2025-11-25", "2025-06-18", "2025-03-26"];

    public static string Negotiate(JsonElement? initializeParams)
    {
        if (initializeParams is { ValueKind: JsonValueKind.Object } p
            && p.TryGetProperty("protocolVersion", out var v)
            && v.ValueKind == JsonValueKind.String
            && Supported.Contains(v.GetString()))
            return v.GetString()!;
        return Supported[0];
    }

    public static string Version => typeof(Protocol).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// The hub's HTTP API revision, in /api/health. Devices update independently, so bump it only
    /// for a change an older shim or CLI can't work with; <c>parley join</c> and <c>status</c>
    /// compare it.
    /// </summary>
    public const int ApiVersion = 1;
}
