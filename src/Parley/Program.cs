using Parley;
using Parley.Hub;
using Parley.Shim;

const string usage = """
    parley — pub/sub topics for AI coding agents

    Usage:
      parley mcp                 MCP server over stdio (what an AI client launches). Starts a hub if none runs.
      parley serve [--background] Run the hub (HTTP on 127.0.0.1:PARLEY_PORT, default 19480).
      parley --version

    Register with Claude Code, and enable push delivery:
      claude mcp add parley -s user -- parley mcp
      claude --dangerously-load-development-channels server:parley

    Environment: PARLEY_SESSION (session name; default: folder name), PARLEY_PORT, PARLEY_URL, PARLEY_STATE.
    """;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

switch (args.FirstOrDefault())
{
    case "mcp":
        return await McpShim.RunStdioAsync(cts.Token);

    case "serve":
        if (args.Contains("--background"))
        {
            // Started by a shim: nobody reads our console, so keep a log instead.
            var logFile = Path.Combine(Path.GetDirectoryName(ParleyConfig.StateFile)!, "hub.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
            var writer = new StreamWriter(logFile, append: false) { AutoFlush = true };
            Console.SetOut(writer);
            Console.SetError(writer);
        }

        var hub = new CollabHub(ParleyConfig.StateFile);
        var app = HubServer.Build(hub, ParleyConfig.Port);
        try
        {
            await app.StartAsync(cts.Token);
        }
        catch (IOException ex)
        {
            Log.Error($"Could not listen on port {ParleyConfig.Port} (is a hub already running?)", ex);
            hub.Dispose();
            return 1;
        }
        Log.Info($"hub listening on http://127.0.0.1:{ParleyConfig.Port} (state: {ParleyConfig.StateFile})");
        await app.WaitForShutdownAsync(cts.Token);
        return 0;

    case "--version":
        Console.WriteLine(Parley.Mcp.Protocol.Version);
        return 0;

    default:
        Console.WriteLine(usage);
        return args.Length == 0 || args[0] is "-h" or "--help" or "help" ? 0 : 1;
}
