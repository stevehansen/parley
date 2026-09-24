using System.Diagnostics;
using System.Net.Http.Json;
using Parley.Hub;
using Parley.Mcp;
using Parley.Sharing;
using Parley.Update;

namespace Parley.Cli;

/// <summary>The human-facing commands: register with AI clients, and inspect a running hub.</summary>
internal static class Commands
{
    private const string ServerName = "parley";

    /// <summary>
    /// Makes Parley "just work": the hub as a login service (always reachable, for agents and the
    /// web UI), and the shim registered with every AI client found on PATH.
    /// </summary>
    public static int Install()
    {
        if (JoinedHub.Load() is { } joined)
            Console.WriteLine($"Hub service: not needed, this device uses the hub at {joined.Url} (`parley leave` to run one here)");
        else if (ServiceManager.ToolShimPath() is { } shim)
            Console.WriteLine($"Hub service: {ServiceManager.Install(shim)}");
        else
            Console.WriteLine("Hub service: skipped (install Parley as a global tool first: dotnet tool install -g HC.Parley). The hub still starts on demand.");

        if (!RegisterClients()) return 1;

        Console.WriteLine($"""

            Done. Restart your AI sessions to load Parley. Web UI: {ParleyConfig.HubUrl}/

            For push delivery (messages wake an idle Claude Code session), start Claude with:
              claude --dangerously-load-development-channels server:{ServerName}
            Without it, agents see unread messages in tool results and can wait with read_messages.
            """);
        return 0;
    }

    /// <summary>Registers the shim with every AI client on PATH; false (with manual instructions) when none was found.</summary>
    public static bool RegisterClients()
    {
        var self = SelfCommand();
        var found = false;

        if (ExternalTool.Find("claude") is { } claude)
        {
            found = true;
            var existing = ExternalTool.Run(claude, "mcp get parley");
            if (existing.exitCode == 0)
            {
                Console.WriteLine("Claude Code: already registered (claude mcp get parley)");
            }
            else
            {
                var (code, output) = ExternalTool.Run(claude, $"mcp add {ServerName} -s user -- {self}");
                Console.WriteLine(code == 0 ? "Claude Code: registered (user scope)" : $"Claude Code: registration failed\n{output}");
            }
        }

        if (ExternalTool.Find("codex") is { } codex)
        {
            found = true;
            var (code, output) = ExternalTool.Run(codex, $"mcp add {ServerName} -- {self}");
            Console.WriteLine(code == 0 ? "Codex: registered" : $"Codex: registration failed (already registered?)\n{output}");
        }

        if (!found)
        {
            Console.WriteLine();
            Console.WriteLine($"""
                No AI client found on PATH. Register manually:
                  claude mcp add {ServerName} -s user -- {self}
                  codex mcp add {ServerName} -- {self}
                """);
        }
        return found;
    }

    public static int Uninstall()
    {
        Console.WriteLine($"Hub service: {ServiceManager.Uninstall()}");
        if (ExternalTool.Find("claude") is { } claude)
            Console.WriteLine(ExternalTool.Run(claude, $"mcp remove {ServerName} -s user").exitCode == 0
                ? "Claude Code: removed" : "Claude Code: not registered");
        if (ExternalTool.Find("codex") is { } codex)
            Console.WriteLine(ExternalTool.Run(codex, $"mcp remove {ServerName}").exitCode == 0
                ? "Codex: removed" : "Codex: not registered");
        return 0;
    }

    public static async Task<int> StatusAsync()
    {
        using var http = ParleyConfig.CreateHubClient(TimeSpan.FromSeconds(3), ParleyConfig.HubToken);
        var url = ParleyConfig.HubUrl;
        var joined = JoinedHub.Load();
        List<Session>? sessions;
        List<Topic>? topics;
        System.Text.Json.JsonElement health;
        try
        {
            health = await http.GetFromJsonAsync<System.Text.Json.JsonElement>($"{url}/api/health");
            sessions = await http.GetFromJsonAsync<List<Session>>($"{url}/api/sessions");
            topics = await http.GetFromJsonAsync<List<Topic>>($"{url}/api/topics");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine(joined != null
                ? $"Hub: can't use {url} ({ex.Message}). Is the overlay network (NetBird, Tailscale) up, and is this device still paired?"
                : $"Hub: not running at {url} (it starts with the first AI session that loads Parley)");
            return 1;
        }

        Console.WriteLine($"Hub: running at {url} — web UI: {url}/");
        if (joined != null)
            Console.WriteLine($"Joined: as '{joined.Device}' (`parley leave` to use a hub on this machine again)");
        else
            Console.WriteLine($"Service: {(ServiceManager.IsInstalled() ? "installed (starts at logon)" : "not installed (hub starts on demand; `parley install` to keep it running)")}");
        if (health.TryGetProperty("apiVersion", out var api) && api.GetInt32() != Protocol.ApiVersion)
            Console.WriteLine($"Version: the hub runs {health.GetProperty("version").GetString()}, this device {Protocol.Version}; update the older one (`parley update`)");
        if (health.TryGetProperty("updateAvailable", out var up) && up.GetBoolean())
            Console.WriteLine($"Update: {health.GetProperty("latest").GetString()} is available — run `parley update`{(joined != null ? " on the hub machine" : "")}");
        if (joined == null && await SharingCommands.PairedDevicesAsync() is { Count: > 0 } paired)
            Console.WriteLine($"Shared with: {string.Join(", ", paired.Select(d => d.CodeExpiresAt != null ? d.Name + " (pairing)" : d.Name))} (`parley devices`)");
        Console.WriteLine();
        Console.WriteLine("Sessions (● = connected, receives pushes):");
        var devicesShown = sessions?.Select(s => s.Device).Distinct().Count() > 1;
        foreach (var s in sessions ?? [])
            Console.WriteLine($"  {(s.Listeners > 0 ? "●" : "○")} {s.Name,-24} {(devicesShown ? $"[{s.Device}] " : "")}{s.WorkingDir}  (last seen {s.LastSeen.ToLocalTime():g})");
        if (sessions is not { Count: > 0 }) Console.WriteLine("  (none)");

        Console.WriteLine();
        Console.WriteLine("Topics:");
        foreach (var t in topics ?? [])
            Console.WriteLine($"  #{t.Name,-23} {t.MessageCount,4} msgs  {string.Join(", ", t.Subscribers)}{(string.IsNullOrEmpty(t.Description) ? "" : $"  — {t.Description}")}");
        if (topics is not { Count: > 0 }) Console.WriteLine("  (none)");
        return 0;
    }

    /// <summary>
    /// The command line AI clients should launch. "parley mcp" when the tool is on PATH — never the
    /// resolved path, which for a dotnet tool sits in a versioned folder that updates delete.
    /// </summary>
    private static string SelfCommand()
    {
        if (ExternalTool.Find("parley") != null) return "parley mcp";
        var exe = Environment.ProcessPath!;
        return Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? $"\"{exe}\" \"{typeof(Commands).Assembly.Location}\" mcp"
            : $"\"{exe}\" mcp";
    }
}

/// <summary>Finds and runs other CLIs, including npm-style <c>.cmd</c> shims on Windows.</summary>
internal static class ExternalTool
{
    public static string? Find(string name)
    {
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in dirs)
        foreach (var ext in exts)
        {
            var candidate = Path.Combine(dir.Trim(), name + ext.ToLowerInvariant());
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static (int exitCode, string output) Run(string path, string args)
    {
        var isScript = OperatingSystem.IsWindows() && Path.GetExtension(path).ToLowerInvariant() is ".cmd" or ".bat";
        var psi = isScript
            ? new ProcessStartInfo("cmd.exe", $"/d /c \"\"{path}\" {args}\"")
            : new ProcessStartInfo(path, args);
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            p.Kill(entireProcessTree: true);
            return (-1, "timed out");
        }
        return (p.ExitCode, (stdout.Result + stderr.Result).Trim());
    }
}
