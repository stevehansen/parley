using System.Diagnostics;

namespace Parley.Shim;

/// <summary>Starts this program again as an independent process (a background hub, an update).</summary>
internal static class SelfProcess
{
    public static void StartDetached(string arguments)
    {
        var (file, args) = SelfCommand(arguments);
        StartDetached(file, args);
    }

    /// <summary>Starts <paramref name="file"/> (another parley, e.g. a freshly updated one) detached.</summary>
    public static void StartDetached(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args);
        if (OperatingSystem.IsWindows())
        {
            // ShellExecute doesn't inherit handles. With plain CreateProcess the child would inherit
            // a shim's stdout — the client's JSON-RPC pipe — and keep it open after we exit.
            psi.UseShellExecute = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            using var _ = Process.Start(psi);
        }
        else
        {
            // Fresh pipes (other fds are close-on-exec); the hub re-points its output to a log file.
            psi.UseShellExecute = false;
            psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
            using var p = Process.Start(psi);
            p?.StandardInput.Close();
        }
    }

    /// <summary>How to re-run this program, whether it runs from its apphost or through <c>dotnet</c>.</summary>
    private static (string file, string args) SelfCommand(string args)
    {
        var exe = Environment.ProcessPath!;
        return Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? (exe, $"exec \"{typeof(SelfProcess).Assembly.Location}\" {args}")
            : (exe, args);
    }
}
