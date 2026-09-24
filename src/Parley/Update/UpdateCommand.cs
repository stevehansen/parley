using System.Diagnostics;
using System.Text.Json;
using Parley.Shim;

namespace Parley.Update;

/// <summary>
/// <c>parley update</c>: installs the newest HC.Parley and brings the hub back on it.
///
/// Only the hub is stopped. AI sessions keep their shims (the old version) running and switch
/// when they next restart; on Windows the files those shims hold open are moved out of the
/// update's way first (<see cref="ToolFiles"/>). The hub comes back afterwards — on the new
/// version, or on the old one if the update failed. The outcome goes to update.log as well,
/// since an update started from the web UI has no console.
/// </summary>
internal static class UpdateCommand
{
    /// <param name="args">
    /// The arguments after <c>update</c>: <c>--check</c> only reports; <c>--to &lt;version&gt;</c>
    /// installs that release; <c>--rollback</c> goes back to the version that ran before this one.
    /// </param>
    public static async Task<int> RunAsync(string[] args)
    {
        var current = UpdateChecker.Current;
        var listed = await new UpdateChecker().ListedAsync();
        if (listed == null)
        {
            Console.WriteLine($"Could not reach NuGet to check for {UpdateChecker.PackageId} releases. Current: {current.ToString(3)}");
            return 1;
        }

        var toIndex = Array.IndexOf(args, "--to");
        Version? target;
        if (toIndex >= 0)
        {
            target = toIndex + 1 < args.Length && Version.TryParse(args[toIndex + 1], out var v) ? UpdateChecker.Normalize(v) : null;
            if (target == null || !listed.Contains(target))
            {
                Console.WriteLine($"Not a listed {UpdateChecker.PackageId} release. Available: {string.Join(", ", listed.Select(x => x.ToString(3)))}");
                return 1;
            }
        }
        else if (args.Contains("--rollback"))
        {
            // Without history (installed by hand, or before history existed): the release below.
            target = UpdateHistory.PredecessorOf(current) ?? listed.LastOrDefault(x => x < current);
            if (target == null)
            {
                Console.WriteLine($"Nothing to roll back to: {current.ToString(3)} is the oldest release.");
                return 1;
            }
        }
        else
        {
            target = listed.LastOrDefault();
            if (target == null || target <= current)
            {
                Console.WriteLine($"Parley {current.ToString(3)} is up to date.");
                return 0;
            }
        }

        if (target == current)
        {
            Console.WriteLine($"Parley {current.ToString(3)} is already installed.");
            return 0;
        }
        Console.WriteLine(target > current
            ? $"Update available: {current.ToString(3)} → {target.ToString(3)}"
            : $"Rolling back: {current.ToString(3)} → {target.ToString(3)}");
        if (args.Contains("--check")) return 0;
        return await InstallAsync(current, target);
    }

    private static async Task<int> InstallAsync(Version current, Version target)
    {
        var toolShim = ServiceManager.ToolShimPath();
        if (toolShim == null)
        {
            Console.WriteLine($"Parley isn't installed as a global dotnet tool here. Update with:\n  dotnet tool update -g {UpdateChecker.PackageId}");
            return 1;
        }

        var version = target.ToString(3);
        var downgrade = target < current;
        var service = ServiceManager.IsInstalled();
        await StopHubAsync(service);

        ToolFiles.DeleteLeftovers(toolShim);
        var moved = OperatingSystem.IsWindows() ? ToolFiles.MoveInUseAside(toolShim) : [];
        var (code, output) = DotnetToolUpdate(version, downgrade);
        // dotnet can exit 0 having installed nothing (stale cache, odd sources): ask the new binary.
        if (code == 0 && InstalledVersion(toolShim) is var installed && installed != version)
        {
            code = 1;
            output = $"dotnet tool update succeeded, but the installed parley reports version {installed ?? "(none)"}.\n{output}";
        }
        if (code != 0) ToolFiles.Restore(moved);

        // A shim may have restarted a hub meanwhile (its reconnect loop does that): replace it too.
        await StopHubAsync(service: false);
        if (service) ServiceManager.Start();
        else SelfProcess.StartDetached(toolShim, "serve --background");

        if (code == 0) UpdateHistory.Append(current, target, rollback: downgrade);
        AppendLog(code == 0
            ? $"{(downgrade ? "rolled back" : "updated")} {current.ToString(3)} to {version}"
            : $"{(downgrade ? "rollback" : "update")} {current.ToString(3)} to {version} FAILED:\n{output.Trim()}");
        Console.WriteLine(code == 0
            ? $"{(downgrade ? "Rolled back" : "Updated")} to {version}; the hub runs it now. Open AI sessions switch when they restart."
            : $"{(downgrade ? "Rollback" : "Update")} failed; still on {current.ToString(3)}.\n{output.Trim()}");
        return code;
    }

    /// <summary>
    /// Stops the running hub — the service's, or one a shim started on demand — so the hub that
    /// starts afterwards is the updated one. Asked to shut down first: killed outright, a hub
    /// loses the subscriptions and read positions of its last few seconds.
    /// </summary>
    private static async Task StopHubAsync(bool service)
    {
        var pid = await HubPidAsync();
        if (pid is { } running && running != Environment.ProcessId)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                using var body = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                (await http.PostAsync($"{ParleyConfig.HubUrl}/api/shutdown", body)).Dispose();
                using var hub = Process.GetProcessById(running);
                hub.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException) { }
        }
        if (service) ServiceManager.Stop();
        if (pid is not { } id || id == Environment.ProcessId) return;
        try
        {
            using var hub = Process.GetProcessById(id);
            if (!hub.HasExited) hub.Kill();
            hub.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private static async Task<int?> HubPidAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var health = JsonDocument.Parse(await http.GetStringAsync($"{ParleyConfig.HubUrl}/api/health"));
            return health.RootElement.TryGetProperty("pid", out var pid) ? pid.GetInt32() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static (int code, string output) DotnetToolUpdate(string version, bool downgrade)
    {
        // --no-http-cache: right after a release, dotnet's cached package index doesn't list it yet.
        var psi = new ProcessStartInfo("dotnet",
            $"tool update -g {UpdateChecker.PackageId} --version {version} --ignore-failed-sources --no-http-cache"
            + (downgrade ? " --allow-downgrade" : ""))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout + stderr.Result);
    }

    private static string? InstalledVersion(string toolShim)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(toolShim, "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            var text = p.StandardOutput.ReadToEnd().Trim();
            return p.WaitForExit(10000) && p.ExitCode == 0 ? text : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static void AppendLog(string line)
    {
        var log = Path.Combine(Path.GetDirectoryName(ParleyConfig.StateFile)!, "update.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
    }

    /// <summary>Starts <c>parley update</c> detached — how the hub's web UI triggers an update.</summary>
    public static void StartDetached() => SelfProcess.StartDetached("update");
}
