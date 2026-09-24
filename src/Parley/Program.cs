using Parley;
using Parley.Cli;
using Parley.Hub;
using Parley.Sharing;
using Parley.Shim;
using Parley.Update;

const string usage = """
    parley — pub/sub topics for AI coding agents

    Usage:
      parley install              Run the hub as a background service and register Parley with Claude Code / Codex
      parley uninstall            Remove the service and those registrations (keeps your conversations)
      parley status               Show the hub, connected sessions and topics
      parley devices              List the devices this hub is shared with
      parley devices add <name>   Pair another device (prints a join command with a one-time code)
      parley devices remove <name> Unpair a device
      parley join <url> <code>    Use another machine's hub from this device
      parley leave                Stop using the joined hub (back to a hub on this machine)
      parley update [--check]     Install the newest release (or just check for one)
      parley update --rollback    Go back to the version that ran before
      parley update --to <ver>    Install a specific release
      parley mcp                  MCP server over stdio — what an AI client launches; starts a hub if none runs
      parley serve [--background] Run the hub yourself (HTTP on 127.0.0.1:PARLEY_PORT, default 19480)
      parley --version

    Web UI: http://127.0.0.1:19480/ while the hub runs.

    Push delivery (a message wakes an idle Claude Code session) needs channels enabled:
      claude --dangerously-load-development-channels server:parley

    Environment: PARLEY_SESSION (session name; default: folder name), PARLEY_PORT, PARLEY_URL, PARLEY_TOKEN, PARLEY_STATE.
    """;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

switch (args.FirstOrDefault())
{
    case "install":
        return Commands.Install();

    case "uninstall":
        return Commands.Uninstall();

    case "status":
        return await Commands.StatusAsync();

    case "devices":
        return await SharingCommands.DevicesAsync(args[1..]);

    case "join":
        return await SharingCommands.JoinAsync(args[1..]);

    case "leave":
        return await SharingCommands.LeaveAsync();

    case "update":
        return await UpdateCommand.RunAsync(args[1..]);

    case "mcp":
        return await McpShim.RunStdioAsync(cts.Token);

    case "serve":
        // Another hub (e.g. auto-started by a shim before the service ran) already serves: that is
        // success, not a crash to restart. Checked before touching hub.log, which that hub holds open.
        if (await HubAlreadyRunningAsync(ParleyConfig.Port))
        {
            if (!args.Contains("--background")) Console.WriteLine($"A Parley hub is already running on port {ParleyConfig.Port}.");
            return 0;
        }
        if (args.Contains("--background"))
        {
            // Started by a shim: nobody reads our console, so keep a log instead.
            var logFile = Path.Combine(Path.GetDirectoryName(ParleyConfig.StateFile)!, "hub.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
            var writer = new StreamWriter(logFile, append: false) { AutoFlush = true };
            Console.SetOut(writer);
            Console.SetError(writer);
            // Started at logon by the scheduled task, a console app gets a console window: drop it.
            if (OperatingSystem.IsWindows()) Native.FreeConsole();
        }

        // Files an earlier update moved aside, now that their shims may have exited.
        if (ServiceManager.ToolShimPath() is { } toolShim) ToolFiles.DeleteLeftovers(toolShim);

        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TerminalHost", "collab-state.json");
        var hub = new CollabHub(ParleyConfig.StateFile, legacy);
        var updates = new UpdateChecker();
        var devices = new DeviceRegistry(ParleyConfig.SharingFile);
        var app = HubServer.Build(hub, ParleyConfig.Port, updates, devices);
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
        Log.Info($"hub {Parley.Mcp.Protocol.Version} listening on http://127.0.0.1:{ParleyConfig.Port} (state: {ParleyConfig.StateFile})");
        _ = updates.RunPeriodicallyAsync(cts.Token);
        await app.WaitForShutdownAsync(cts.Token);
        return 0;

    case "--version" or "version":
        Console.WriteLine(Parley.Mcp.Protocol.Version);
        return 0;

    default:
        Console.WriteLine(usage);
        return args.Length == 0 || args[0] is "-h" or "--help" or "help" ? 0 : 1;
}

// Binding first keeps the common case (port free) instant; only a taken port costs a health probe.
static async Task<bool> HubAlreadyRunningAsync(int port)
{
    try
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        probe.Start();
        probe.Stop();
        return false;
    }
    catch (System.Net.Sockets.SocketException)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var health = await http.GetStringAsync($"http://127.0.0.1:{port}/api/health");
            return health.Contains("\"name\":\"parley\"");
        }
        catch (Exception)
        {
            return false;
        }
    }
}

internal static class Native
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    internal static extern bool FreeConsole();
}
