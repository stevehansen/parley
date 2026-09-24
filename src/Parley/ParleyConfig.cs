using System.Net.Http.Headers;
using Parley.Sharing;

namespace Parley;

/// <summary>Where the hub listens and keeps its state. Overridable through environment variables.</summary>
internal static class ParleyConfig
{
    public const int DefaultPort = 19480;

    /// <summary>Hub port for <c>parley serve</c> (PARLEY_PORT).</summary>
    public static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable("PARLEY_PORT"), out var p) ? p : DefaultPort;

    /// <summary>Hub base URL the MCP shim talks to: PARLEY_URL, else the joined hub, else this machine's.</summary>
    public static string HubUrl => Hub().url;

    /// <summary>This device's credential for <see cref="HubUrl"/>: PARLEY_TOKEN, else the joined hub's.</summary>
    public static string? HubToken => Hub().token;

    // The joined hub's token only ever goes to the joined hub, never to a PARLEY_URL override.
    private static (string url, string? token) Hub()
    {
        var envToken = Environment.GetEnvironmentVariable("PARLEY_TOKEN") is { Length: > 0 } t ? t : null;
        if (Environment.GetEnvironmentVariable("PARLEY_URL") is { Length: > 0 } url) return (url.TrimEnd('/'), envToken);
        if (JoinedHub.Load() is { } joined) return (joined.Url.TrimEnd('/'), envToken ?? joined.Token);
        return (LocalHubUrl, envToken);
    }

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

    /// <summary>Who this machine's hub is shared with (see <see cref="DeviceRegistry"/>), next to the state.</summary>
    public static string SharingFile => Path.Combine(Path.GetDirectoryName(StateFile)!, "sharing.json");

    /// <summary>An HttpClient for a hub: carries the device token (e.g. <see cref="HubToken"/>) and our Parley version.</summary>
    public static HttpClient CreateHubClient(TimeSpan timeout, string? token)
    {
        var http = new HttpClient { Timeout = timeout };
        if (token is { Length: > 0 } t)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t);
        http.DefaultRequestHeaders.Add(VersionHeader, Mcp.Protocol.Version);
        return http;
    }

    /// <summary>Request header naming the caller's Parley version, so the hub can list who is behind.</summary>
    public const string VersionHeader = "X-Parley-Version";
}
