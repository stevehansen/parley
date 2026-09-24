using System.Diagnostics;
using Parley.Shim;

namespace Parley.Update;

/// <summary>
/// <c>parley update</c>: installs the newest HC.Parley and brings the hub back on it.
///
/// Every running parley process (the hub and each session's shim) holds the tool's files open,
/// and on Windows that blocks <c>dotnet tool update</c>. So on Windows the update runs from a
/// small script after this process exits: it kills parley processes and retries the update to
/// out-race AI clients respawning their shim. Either way the service is started again afterwards,
/// on the new version or — if the update failed — the old one. Open AI sessions reconnect on
/// their own (Claude Code respawns stdio servers) or via /mcp.
/// </summary>
internal static class UpdateCommand
{
    public static async Task<int> RunAsync(bool checkOnly)
    {
        var current = UpdateChecker.Current;
        var latest = await new UpdateChecker().CheckAsync();
        if (latest == null)
        {
            Console.WriteLine($"Could not reach NuGet to check for {UpdateChecker.PackageId} updates. Current: {current.ToString(3)}");
            return 1;
        }
        if (latest <= current)
        {
            Console.WriteLine($"Parley {current.ToString(3)} is up to date.");
            return 0;
        }

        Console.WriteLine($"Update available: {current.ToString(3)} → {latest.ToString(3)}");
        if (checkOnly) return 0;

        if (ServiceManager.ToolShimPath() == null)
        {
            Console.WriteLine($"Parley isn't installed as a global dotnet tool here. Update with:\n  dotnet tool update -g {UpdateChecker.PackageId}");
            return 1;
        }

        var restartService = ServiceManager.IsInstalled();
        if (restartService) ServiceManager.Stop();

        var version = latest.ToString(3);
        return OperatingSystem.IsWindows()
            ? StartWindowsTrampoline(current.ToString(3), version, restartService)
            : UpdateInPlace(version, restartService);
    }

    private static int UpdateInPlace(string version, bool restartService)
    {
        KillOtherParleyProcesses();
        var psi = new ProcessStartInfo("dotnet", $"tool update -g {UpdateChecker.PackageId} --version {version} --ignore-failed-sources --no-http-cache");
        using var p = Process.Start(psi)!;
        p.WaitForExit();

        if (restartService)
        {
            var parts = ServiceManager.StartCommand().Split(' ', 2);
            using var start = Process.Start(parts[0], parts[1]);
            start?.WaitForExit();
        }

        Console.WriteLine(p.ExitCode == 0
            ? $"Updated to {version}. Restart AI sessions (or /mcp → reconnect) to load the new shim."
            : "Update failed; still on the previous version.");
        return p.ExitCode;
    }

    private static int StartWindowsTrampoline(string current, string version, bool restartService)
    {
        var log = Path.Combine(Path.GetDirectoryName(ParleyConfig.StateFile)!, "update.log");
        var script = Path.Combine(Path.GetTempPath(), $"parley-update-{Guid.NewGuid():N}.cmd");
        var pid = Environment.ProcessId;
        File.WriteAllText(script, $"""
            @echo off
            setlocal
            set /a TRIES=0
            :WAIT
            tasklist /fi "PID eq {pid}" 2>nul | find "{pid}" >nul
            if errorlevel 1 goto UPDATE
            set /a TRIES+=1
            if %TRIES% geq 30 goto UPDATE
            timeout /t 1 /nobreak >nul
            goto WAIT

            :UPDATE
            set /a ATTEMPT=0
            :RETRY
            set /a ATTEMPT+=1
            REM AI clients respawn their parley shim right away, re-locking the tool: kill, then update at once.
            taskkill /f /im parley.exe >nul 2>&1
            dotnet tool update -g {UpdateChecker.PackageId} --version {version} --ignore-failed-sources --no-http-cache > "%TEMP%\parley-update-output.txt" 2>&1
            if not errorlevel 1 (
                echo %date% %time% updated {current} to {version} >> "{log}"
                goto RESTART
            )
            if %ATTEMPT% lss 5 (
                timeout /t 2 /nobreak >nul
                goto RETRY
            )
            echo %date% %time% update {current} to {version} FAILED: >> "{log}"
            type "%TEMP%\parley-update-output.txt" >> "{log}"

            :RESTART
            {(restartService ? ServiceManager.StartCommand() + " >nul 2>&1" : "REM hub starts with the next AI session")}
            del "%~f0"
            """);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c \"{script}\"")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        })?.Dispose();

        Console.WriteLine($"""
            Updating to {version} in the background (log: {log}).
            The hub restarts in a few seconds; AI sessions reconnect on their own, or via /mcp → reconnect.
            """);
        return 0;
    }

    private static void KillOtherParleyProcesses()
    {
        foreach (var p in Process.GetProcessesByName("parley"))
        {
            using (p)
            {
                if (p.Id == Environment.ProcessId) continue;
                try { p.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    /// <summary>Starts <c>parley update</c> detached — how the hub's web UI triggers an update.</summary>
    public static void StartDetached() => SelfProcess.StartDetached("update");
}
