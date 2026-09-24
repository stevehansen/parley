using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;

namespace Parley.Sharing;

/// <summary>
/// Kestrel's endpoint list, kept current: loopback always, and while the hub is shared also every
/// local address inside <see cref="DeviceRegistry.AllowFrom"/> (the overlay's, e.g. NetBird's 100.x).
///
/// Specific addresses, never 0.0.0.0: next to a wildcard bind a second hub can still claim
/// 127.0.0.1 (Windows lets the more specific bind win) and would take over local traffic, and a
/// wildcard also exposes the hub on every network. Re-evaluated when sharing changes and when the
/// machine's addresses change, so an overlay that connects after the hub started is picked up.
/// Kestrel rebinds only the endpoints that changed; the loopback one is never dropped.
/// </summary>
internal sealed class HubEndpoints : ConfigurationProvider, IConfigurationSource, IDisposable
{
    private readonly int _port;
    private readonly DeviceRegistry _devices;
    private readonly object _lock = new();
    private IReadOnlyList<IPAddress> _network = [];

    public HubEndpoints(int port, DeviceRegistry devices)
    {
        _port = port;
        _devices = devices;
        Data = Compute(out _network);
        devices.SharingChanged += Refresh;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
    }

    /// <summary>The non-loopback addresses the hub listens on right now (empty while not shared).</summary>
    public IReadOnlyList<IPAddress> NetworkAddresses
    {
        get
        {
            lock (_lock) return _network;
        }
    }

    /// <summary>The configuration section to hand <c>KestrelServerOptions.Configure</c>.</summary>
    public IConfiguration Configuration => new ConfigurationBuilder().Add(this).Build();

    IConfigurationProvider IConfigurationSource.Build(IConfigurationBuilder builder) => this;

    public void Refresh()
    {
        lock (_lock)
        {
            var data = Compute(out var network);
            if (data.Count == Data.Count && data.All(kv => Data.TryGetValue(kv.Key, out var v) && v == kv.Value)) return;
            Data = data;
            _network = network;
            if (network.Count > 0) Log.Info($"sharing on {string.Join(", ", network)}");
            else Log.Info("listening on loopback only");
        }
        OnReload();
    }

    public void Dispose()
    {
        _devices.SharingChanged -= Refresh;
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
    }

    private void OnAddressChanged(object? sender, EventArgs e) => Refresh();

    private Dictionary<string, string?> Compute(out IReadOnlyList<IPAddress> network)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Endpoints:Loopback:Url"] = $"http://127.0.0.1:{_port}",
        };
        network = _devices.IsSharing ? LocalAddresses().Where(_devices.IsAllowed).ToList() : [];
        for (var i = 0; i < network.Count; i++)
            data[$"Endpoints:Net{i}:Url"] = $"http://{Format(network[i])}:{_port}";
        return data;
    }

    private static IEnumerable<IPAddress> LocalAddresses()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
        return interfaces
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
            .Where(a => !IPAddress.IsLoopback(a) && a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Distinct()
            .OrderBy(a => a.ToString(), StringComparer.Ordinal);
    }

    internal static string Format(IPAddress a) => a.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{a}]" : a.ToString();
}
