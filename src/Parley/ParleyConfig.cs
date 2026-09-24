namespace Parley;

/// <summary>Where the hub listens and keeps its state. Overridable through environment variables.</summary>
internal static class ParleyConfig
{
    public const int DefaultPort = 19480;

    /// <summary>Hub port for <c>parley serve</c> (PARLEY_PORT).</summary>
    public static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable("PARLEY_PORT"), out var p) ? p : DefaultPort;

    /// <summary>Hub base URL the MCP shim talks to (PARLEY_URL).</summary>
    public static string HubUrl =>
        (Environment.GetEnvironmentVariable("PARLEY_URL") ?? LocalHubUrl).TrimEnd('/');

    /// <summary>
    /// Where a hub on this machine answers, whatever <see cref="HubUrl"/> points at. Anything that
    /// acts on the hub's process (stop, restart, kill by pid) must use this: a remote hub's pid means
    /// nothing here.
    /// </summary>
    public static string LocalHubUrl => $"http://127.0.0.1:{Port}";

    /// <summary>State file (PARLEY_STATE); %APPDATA%\Parley on Windows, ~/.config/Parley elsewhere.</summary>
    public static string StateFile =>
        Environment.GetEnvironmentVariable("PARLEY_STATE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Parley", "state.json");
}
