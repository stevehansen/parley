namespace Parley;

/// <summary>
/// Diagnostics go to stderr only: in <c>parley mcp</c> stdout is the JSON-RPC channel, and Claude
/// Code captures stderr into its MCP logs.
/// </summary>
internal static class Log
{
    public static void Info(string msg) => Console.Error.WriteLine($"[parley {DateTime.Now:HH:mm:ss}] {msg}");

    public static void Error(string msg, Exception? ex = null) =>
        Console.Error.WriteLine($"[parley {DateTime.Now:HH:mm:ss}] ERROR {msg}{(ex != null ? $": {ex.Message}" : "")}");
}
