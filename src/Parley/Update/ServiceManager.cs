using System.Diagnostics;
using System.Security;
using Parley.Cli;

namespace Parley.Update;

/// <summary>
/// Keeps the hub running for the logged-in user: a logon scheduled task on Windows, a launchd
/// agent on macOS, a systemd user unit on Linux. All restart it if it dies. The service runs the
/// dotnet-tool shim (~/.dotnet/tools/parley), so updating the tool updates the service.
/// </summary>
internal static class ServiceManager
{
    private const string TaskName = "Parley";
    private const string LaunchdLabel = "dev.parley.hub";

    public static string? ToolShimPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var shim = Path.Combine(home, ".dotnet", "tools", OperatingSystem.IsWindows() ? "parley.exe" : "parley");
        return File.Exists(shim) ? shim : null;
    }

    /// <summary>Registers and starts the service. Returns a one-line description of what happened.</summary>
    public static string Install(string shimPath)
    {
        if (OperatingSystem.IsWindows()) return InstallScheduledTask(shimPath);
        if (OperatingSystem.IsMacOS()) return InstallLaunchd(shimPath);
        return InstallSystemd(shimPath);
    }

    public static string Uninstall()
    {
        if (OperatingSystem.IsWindows())
        {
            Run("schtasks.exe", $"/end /tn {TaskName}");
            return Run("schtasks.exe", $"/delete /tn {TaskName} /f").exitCode == 0 ? "scheduled task removed" : "no scheduled task";
        }
        if (OperatingSystem.IsMacOS())
        {
            var plist = LaunchdPlistPath();
            if (!File.Exists(plist)) return "no launchd agent";
            Run("launchctl", $"unload \"{plist}\"");
            File.Delete(plist);
            return "launchd agent removed";
        }

        var unit = SystemdUnitPath();
        if (!File.Exists(unit)) return "no systemd unit";
        Run("systemctl", "--user disable --now parley.service");
        File.Delete(unit);
        Run("systemctl", "--user daemon-reload");
        return "systemd unit removed";
    }

    public static bool IsInstalled()
    {
        if (OperatingSystem.IsWindows()) return Run("schtasks.exe", $"/query /tn {TaskName}").exitCode == 0;
        if (OperatingSystem.IsMacOS()) return File.Exists(LaunchdPlistPath());
        return File.Exists(SystemdUnitPath());
    }

    public static void Stop()
    {
        if (OperatingSystem.IsWindows()) Run("schtasks.exe", $"/end /tn {TaskName}");
        else if (OperatingSystem.IsMacOS()) Run("launchctl", $"stop {LaunchdLabel}");
        else Run("systemctl", "--user stop parley.service");
    }

    /// <summary>Shell command that starts the service again; used by the update trampoline.</summary>
    public static string StartCommand() =>
        OperatingSystem.IsWindows() ? $"schtasks.exe /run /tn {TaskName}"
        : OperatingSystem.IsMacOS() ? $"launchctl start {LaunchdLabel}"
        : "systemctl --user start parley.service";

    private static string InstallScheduledTask(string shimPath)
    {
        Run("schtasks.exe", $"/delete /tn {TaskName} /f");
        // Naming the user matters: a logon trigger without one fires for every user, which only an
        // elevated shell may register ("Access is denied" otherwise).
        var user = SecurityElement.Escape($@"{Environment.UserDomainName}\{Environment.UserName}");
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Parley hub — pub/sub topics for AI coding agents</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal>
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>999</Count>
                </RestartOnFailure>
              </Settings>
              <Actions>
                <Exec>
                  <Command>{SecurityElement.Escape(shimPath)}</Command>
                  <Arguments>serve --background</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
        var xmlPath = Path.Combine(Path.GetTempPath(), $"parley-task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(xmlPath, xml, System.Text.Encoding.Unicode);
        try
        {
            var (code, output) = Run("schtasks.exe", $"/create /tn {TaskName} /xml \"{xmlPath}\" /f");
            if (code != 0) return $"could not create scheduled task: {output}";
        }
        finally
        {
            File.Delete(xmlPath);
        }
        return Run("schtasks.exe", $"/run /tn {TaskName}").exitCode == 0
            ? "scheduled task registered and started (runs at logon)"
            : "scheduled task registered (starts at next logon)";
    }

    private static string LaunchdPlistPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", $"{LaunchdLabel}.plist");

    private static string InstallLaunchd(string shimPath)
    {
        var plist = LaunchdPlistPath();
        Directory.CreateDirectory(Path.GetDirectoryName(plist)!);
        File.WriteAllText(plist, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{LaunchdLabel}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{SecurityElement.Escape(shimPath)}</string>
                    <string>serve</string>
                    <string>--background</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <true/>
            </dict>
            </plist>
            """);
        Run("launchctl", $"unload \"{plist}\"");
        return Run("launchctl", $"load \"{plist}\"").exitCode == 0
            ? "launchd agent registered and loaded"
            : $"launchd plist written to {plist} (load it with: launchctl load \"{plist}\")";
    }

    private static string SystemdUnitPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "systemd", "user", "parley.service");

    private static string InstallSystemd(string shimPath)
    {
        var unit = SystemdUnitPath();
        Directory.CreateDirectory(Path.GetDirectoryName(unit)!);
        File.WriteAllText(unit, $"""
            [Unit]
            Description=Parley hub — pub/sub topics for AI coding agents

            [Service]
            Type=simple
            ExecStart="{shimPath}" serve --background
            Restart=on-failure
            RestartSec=5

            [Install]
            WantedBy=default.target
            """);
        Run("systemctl", "--user daemon-reload");
        return Run("systemctl", "--user enable --now parley.service").exitCode == 0
            ? "systemd user service enabled and started"
            : $"systemd unit written to {unit} (start it with: systemctl --user enable --now parley)";
    }

    private static (int exitCode, string output) Run(string file, string args)
    {
        try
        {
            return ExternalTool.Run(file, args);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }
}
