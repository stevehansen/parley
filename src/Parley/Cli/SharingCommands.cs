using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Parley.Mcp;
using Parley.Sharing;
using Parley.Update;

namespace Parley.Cli;

/// <summary>
/// Using one hub from several devices. On the hub machine, <c>parley devices</c> pairs and
/// unpairs them (through the local hub's loopback API); on another device, <c>parley join</c> /
/// <c>leave</c> point this device's sessions at that hub.
/// </summary>
internal static class SharingCommands
{
    public static async Task<int> DevicesAsync(string[] args)
    {
        if (JoinedHub.Load() is { } joined)
        {
            Console.WriteLine($"This device uses the hub at {joined.Url}: manage devices there.");
            return 1;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var hub = ParleyConfig.LocalHubUrl;
        try
        {
            switch (args.FirstOrDefault())
            {
                case null:
                    return await ListAsync(http, hub);
                case "add" when args.Length == 2:
                    return await AddAsync(http, hub, args[1]);
                case "remove" when args.Length == 2:
                    using (var r = await http.DeleteAsync($"{hub}/api/devices/{Uri.EscapeDataString(args[1])}"))
                    {
                        Console.WriteLine(r.IsSuccessStatusCode ? $"Removed '{args[1]}'." : $"No device named '{args[1]}'.");
                        return r.IsSuccessStatusCode ? 0 : 1;
                    }
                default:
                    Console.WriteLine("Usage: parley devices [add <name> | remove <name>]");
                    return 1;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"The hub isn't running on this machine ({hub}). Start it with `parley install` (or `parley serve`).");
            return 1;
        }
    }

    /// <summary>The local hub's devices, or null when no hub runs here.</summary>
    public static async Task<List<DeviceInfo>?> PairedDevicesAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            return await http.GetFromJsonAsync<List<DeviceInfo>>($"{ParleyConfig.LocalHubUrl}/api/devices", Json.Options);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static async Task<int> ListAsync(HttpClient http, string hub)
    {
        var devices = await http.GetFromJsonAsync<List<DeviceInfo>>($"{hub}/api/devices", Json.Options) ?? [];
        if (devices.Count == 0)
        {
            Console.WriteLine("Not shared with any device: the hub only listens on this machine. Pair one with `parley devices add <name>`.");
            return 0;
        }
        foreach (var d in devices)
            Console.WriteLine(d.CodeExpiresAt is { } expires
                ? $"  {d.Name,-16} pairing code pending (expires {expires.ToLocalTime():t})"
                : $"  {d.Name,-16} paired {d.PairedAt?.ToLocalTime():d}, last seen {(d.LastSeen is { } seen ? seen.ToLocalTime().ToString("g") : "never")}{(d.Version != null ? $", Parley {d.Version}" : "")}");
        return 0;
    }

    private static async Task<int> AddAsync(HttpClient http, string hub, string name)
    {
        using var r = await http.PostAsJsonAsync($"{hub}/api/devices", new { name });
        using var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        if (!r.IsSuccessStatusCode)
        {
            Console.WriteLine(body.RootElement.TryGetProperty("error", out var e) ? e.GetString() : $"The hub refused ({(int)r.StatusCode}).");
            return 1;
        }

        var root = body.RootElement;
        var code = root.GetProperty("code").GetString();
        var urls = root.GetProperty("urls").EnumerateArray().Select(u => u.GetString()!).ToList();
        var allowFrom = string.Join(", ", root.GetProperty("allowFrom").EnumerateArray().Select(n => n.GetString()));
        var minutes = (int)DeviceRegistry.CodeLifetime.TotalMinutes;

        if (urls.Count == 0)
        {
            Console.WriteLine($"""
                Pairing code for '{name}': {code} (single use, valid {minutes} minutes)

                This machine has no address in {allowFrom} right now, so nothing can reach the hub yet.
                Is NetBird (or Tailscale) connected? The hub starts listening as soon as an address appears.
                On {name}, run: parley join http://<this machine's overlay address>:{ParleyConfig.Port} {code}
                """);
            return 0;
        }

        Console.WriteLine($"""
            Pairing code for '{name}': {code} (single use, valid {minutes} minutes)

            On {name}, run:
              parley join {urls[0]} {code}

            From a browser (e.g. a phone): open {urls[0]}/ and enter the code.
            Its overlay DNS name works in place of the address too (NetBird's default: {Environment.MachineName.ToLowerInvariant()}.netbird.cloud).
            """);
        if (OperatingSystem.IsWindows())
            Console.WriteLine($"""
                If {name} can't connect, Windows Firewall may be blocking the port. Allow it (admin PowerShell):
                  New-NetFirewallRule -DisplayName "Parley hub" -Direction Inbound -Protocol TCP -LocalPort {ParleyConfig.Port} -RemoteAddress {allowFrom.Replace(" ", "")} -Action Allow
                """);
        return 0;
    }

    public static async Task<int> JoinAsync(string[] args)
    {
        if (args.Length != 2)
        {
            Console.WriteLine("Usage: parley join <hub url> <pairing code>   (get both with `parley devices add <name>` on the hub machine)");
            return 1;
        }
        if (NormalizeUrl(args[0]) is not { } url)
        {
            Console.WriteLine($"'{args[0]}' isn't a hub address. Example: http://desktop.netbird.cloud:{ParleyConfig.DefaultPort}");
            return 1;
        }

        string token, device;
        try
        {
            using var http = ParleyConfig.CreateHubClient(TimeSpan.FromSeconds(10), null);
            using var r = await http.PostAsJsonAsync($"{url}/api/pair", new { code = args[1] });
            var text = await r.Content.ReadAsStringAsync();
            if (r.StatusCode == HttpStatusCode.NotFound)
            {
                Console.WriteLine($"{url} doesn't look like a Parley hub (or it runs a version without sharing: update it).");
                return 1;
            }
            using var body = JsonDocument.Parse(text);
            if (!r.IsSuccessStatusCode)
            {
                Console.WriteLine($"The hub refused: {(body.RootElement.TryGetProperty("error", out var e) ? e.GetString() : $"HTTP {(int)r.StatusCode}")}");
                return 1;
            }
            token = body.RootElement.GetProperty("token").GetString()!;
            device = body.RootElement.GetProperty("device").GetString()!;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Console.WriteLine($"Can't reach a hub at {url}: {ex.Message}\nIs the overlay network (NetBird, Tailscale) connected on both machines?");
            return 1;
        }

        new JoinedHub { Url = url, Token = token, Device = device }.Save();
        Console.WriteLine($"Joined the hub at {url} as '{device}'.");

        using (var http = ParleyConfig.CreateHubClient(TimeSpan.FromSeconds(5), token))
        {
            try
            {
                using var health = JsonDocument.Parse(await http.GetStringAsync($"{url}/api/health"));
                if (health.RootElement.GetProperty("apiVersion").GetInt32() != Protocol.ApiVersion)
                    Console.WriteLine($"Note: the hub runs Parley {health.RootElement.GetProperty("version").GetString()}, this device {Protocol.Version}. Update the older one.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException) { }
        }

        // A hub here would only start again at logon, with nobody using it.
        if (ServiceManager.IsInstalled())
            Console.WriteLine($"Local hub service: {ServiceManager.Uninstall()} (conversations kept; `parley leave` and `parley install` bring it back)");
        Commands.RegisterClients();
        Console.WriteLine($"""

            Restart your AI sessions to switch them to this hub. Web UI: {url}/ (a browser needs its own code from `parley devices add`).
            """);
        return 0;
    }

    public static async Task<int> LeaveAsync()
    {
        if (JoinedHub.Load() is not { } joined)
        {
            Console.WriteLine("This device isn't joined to another hub.");
            return 1;
        }
        try
        {
            using var http = ParleyConfig.CreateHubClient(TimeSpan.FromSeconds(5), joined.Token);
            using var body = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            (await http.PostAsync($"{joined.Url}/api/unpair", body)).Dispose();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"Couldn't reach {joined.Url} to unpair; remove '{joined.Device}' there with `parley devices remove {joined.Device}`.");
        }
        JoinedHub.Delete();
        Console.WriteLine($"""
            Left the hub at {joined.Url}. Restart your AI sessions: they use a hub on this machine again
            (started on demand; `parley install` keeps one running).
            """);
        return 0;
    }

    /// <summary>"desktop.netbird.cloud" → "http://desktop.netbird.cloud:19480"; null if it can't be a URL.</summary>
    internal static string? NormalizeUrl(string raw)
    {
        var s = raw.Trim().TrimEnd('/');
        if (!s.Contains("://")) s = "http://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0) return null;
        var builder = new UriBuilder(uri);
        // A bare http address means the hub itself; https implies a proxy in front, on its own port.
        if (uri.Scheme == "http" && uri.IsDefaultPort && !s.Contains(":80")) builder.Port = ParleyConfig.DefaultPort;
        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }
}
