using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Parley.Sharing;

/// <summary>
/// The other devices allowed to use this hub, and the networks they may come from.
///
/// A device pairs with a one-time code shown on the hub machine (<see cref="CreatePairingCode"/>)
/// and trades it for a long-lived token (<see cref="Redeem"/>). Only the token's SHA-256 is kept,
/// in sharing.json next to the hub state. The hub is shared, meaning it listens beyond loopback,
/// exactly while a device is paired or a code is pending (<see cref="IsSharing"/>): removing the
/// last device closes the network side again, with no separate switch to forget.
/// </summary>
public sealed partial class DeviceRegistry : IDisposable
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    // Codes carry ~45 bits; after this many wrong guesses every pending code is dropped anyway.
    private const int MaxFailedRedeems = 10;
    private const string CodeAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ"; // no 0/O, 1/I/L
    private static readonly string[] DefaultAllowFrom = ["100.64.0.0/10"]; // NetBird, Tailscale (CGNAT)
    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly object _lock = new();
    private readonly string? _file;
    private readonly IPNetwork[] _allowFrom;
    private readonly Dictionary<string, Device> _byTokenHash = new();
    private readonly Dictionary<string, PendingCode> _codes = new();
    private int _failedRedeems;

    /// <summary>Raised (outside the lock) when <see cref="IsSharing"/> may have changed.</summary>
    public event Action? SharingChanged;

    /// <param name="file">sharing.json, or null to keep everything in memory.</param>
    /// <param name="allowFrom">Overrides the networks from the file (tests).</param>
    public DeviceRegistry(string? file = null, IEnumerable<string>? allowFrom = null)
    {
        _file = file;
        var persisted = Load();
        foreach (var d in persisted?.Devices ?? []) _byTokenHash[d.TokenSha256] = d;
        _allowFrom = (allowFrom ?? persisted?.AllowFrom ?? (IEnumerable<string>)DefaultAllowFrom)
            .Select(n => IPNetwork.Parse(n.Trim())).ToArray();
    }

    public bool IsSharing
    {
        get
        {
            lock (_lock) return _byTokenHash.Count > 0 || _codes.Values.Any(c => !c.Expired);
        }
    }

    /// <summary>The networks, besides loopback, that may reach the hub while it is shared.</summary>
    public IReadOnlyList<IPNetwork> AllowFrom => _allowFrom;

    public bool IsAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return _allowFrom.Any(n => n.Contains(address));
    }

    /// <summary>
    /// A code that pairs the next device to redeem it under <paramref name="deviceName"/>,
    /// replacing any device (and pending code) of that name. Valid for <see cref="CodeLifetime"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The name isn't a short lowercase word (letters, digits, dashes).</exception>
    public string CreatePairingCode(string deviceName)
    {
        var name = NormalizeName(deviceName);
        var code = new string(RandomNumberGenerator.GetItems<char>(CodeAlphabet, 9));
        lock (_lock)
        {
            foreach (var (k, c) in _codes.Where(x => x.Value.DeviceName == name || x.Value.Expired).ToList())
            {
                c.Timer.Dispose();
                _codes.Remove(k);
            }
            // Re-evaluated at expiry: the last pending code lapsing can end sharing.
            var timer = new Timer(_ => ExpireCodes(), null, CodeLifetime + TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
            _codes[code] = new PendingCode(name, DateTime.UtcNow + CodeLifetime, timer);
        }
        SharingChanged?.Invoke();
        return $"{code[..3]}-{code[3..6]}-{code[6..]}";
    }

    /// <summary>Trades a pairing code for a new device and its token; null when the code is wrong or expired.</summary>
    public (Device device, string token)? Redeem(string code)
    {
        var key = code.Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant();
        (Device, string)? result = null;
        var revoked = new List<Device>();
        var dropped = false;
        lock (_lock)
        {
            if (!_codes.Remove(key, out var pending) || pending.Expired)
            {
                pending?.Timer.Dispose();
                if (++_failedRedeems >= MaxFailedRedeems)
                {
                    foreach (var c in _codes.Values) c.Timer.Dispose();
                    dropped = _codes.Count > 0;
                    _codes.Clear();
                    _failedRedeems = 0;
                }
            }
            else
            {
                pending.Timer.Dispose();
                _failedRedeems = 0;
                foreach (var (hash, d) in _byTokenHash.Where(x => x.Value.Name == pending.DeviceName).ToList())
                {
                    _byTokenHash.Remove(hash);
                    revoked.Add(d);
                }
                var token = "prly_" + Base64Url(RandomNumberGenerator.GetBytes(32));
                var device = new Device { Name = pending.DeviceName, TokenSha256 = Hash(token), PairedAt = DateTime.UtcNow };
                _byTokenHash[device.TokenSha256] = device;
                Save();
                result = (device, token);
            }
        }
        foreach (var d in revoked) d.Revoke();
        if (result != null || dropped) SharingChanged?.Invoke();
        return result;
    }

    /// <summary>The device a token belongs to, or null.</summary>
    public Device? Authenticate(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = Hash(token);
        lock (_lock) return _byTokenHash.GetValueOrDefault(hash);
    }

    /// <summary>Unpairs a device (and drops a pending code for that name); its open requests are aborted.</summary>
    public bool Remove(string name)
    {
        name = name.Trim().ToLowerInvariant();
        List<Device> removed;
        bool dropped;
        lock (_lock)
        {
            removed = _byTokenHash.Where(x => x.Value.Name == name).Select(x => x.Value).ToList();
            foreach (var d in removed) _byTokenHash.Remove(d.TokenSha256);
            var codes = _codes.Where(x => x.Value.DeviceName == name).ToList();
            foreach (var (k, c) in codes)
            {
                c.Timer.Dispose();
                _codes.Remove(k);
            }
            dropped = codes.Count > 0;
            if (removed.Count > 0) Save();
        }
        foreach (var d in removed) d.Revoke();
        if (removed.Count > 0 || dropped) SharingChanged?.Invoke();
        return removed.Count > 0 || dropped;
    }

    public List<DeviceInfo> List()
    {
        lock (_lock)
            return _byTokenHash.Values
                .Select(d => new DeviceInfo(d.Name, d.PairedAt, d.LastSeen, d.Version, null))
                .Concat(_codes.Values.Where(c => !c.Expired)
                    .Select(c => new DeviceInfo(c.DeviceName, null, null, null, c.ExpiresAt)))
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .ToList();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var c in _codes.Values) c.Timer.Dispose();
            _codes.Clear();
        }
    }

    private void ExpireCodes()
    {
        lock (_lock)
            foreach (var (k, c) in _codes.Where(x => x.Value.Expired).ToList())
            {
                c.Timer.Dispose();
                _codes.Remove(k);
            }
        SharingChanged?.Invoke();
    }

    internal static string NormalizeName(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        if (!DeviceName().IsMatch(n))
            throw new ArgumentException($"'{name}' isn't a usable device name: use letters, digits and dashes, up to 32 characters (e.g. laptop, phone).");
        return n;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex DeviceName();

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private SharingFile? Load()
    {
        if (_file == null || !File.Exists(_file)) return null;
        try
        {
            return JsonSerializer.Deserialize<SharingFile>(File.ReadAllText(_file), FileJson);
        }
        catch (JsonException ex)
        {
            Log.Error($"ignoring unreadable {_file}", ex);
            return null;
        }
    }

    // Called under the lock.
    private void Save()
    {
        if (_file == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new SharingFile
        {
            AllowFrom = _allowFrom.Select(n => n.ToString()).ToList(),
            Devices = _byTokenHash.Values.OrderBy(d => d.Name, StringComparer.Ordinal).ToList(),
        }, FileJson));
        File.Move(tmp, _file, overwrite: true);
    }

    private sealed record PendingCode(string DeviceName, DateTime ExpiresAt, Timer Timer)
    {
        public bool Expired => DateTime.UtcNow >= ExpiresAt;
    }

    private sealed class SharingFile
    {
        [JsonPropertyName("allowFrom")]
        public List<string>? AllowFrom { get; set; }

        [JsonPropertyName("devices")]
        public List<Device> Devices { get; set; } = new();
    }
}

/// <summary>A paired device. <see cref="Revoked"/> fires when it is unpaired.</summary>
public sealed class Device
{
    private readonly CancellationTokenSource _revoked = new();

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("tokenSha256")]
    public string TokenSha256 { get; set; } = "";

    [JsonPropertyName("pairedAt")]
    public DateTime PairedAt { get; set; }

    [JsonPropertyName("lastSeen")]
    public DateTime? LastSeen { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonIgnore]
    public CancellationToken Revoked => _revoked.Token;

    /// <summary>Records that the device was just heard from, and on which Parley version.</summary>
    public void Touch(string? version)
    {
        LastSeen = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(version)) Version = version;
    }

    internal void Revoke() => _revoked.Cancel();
}

/// <summary>A device as listed: paired (<see cref="PairedAt"/> set) or waiting for its code (<see cref="CodeExpiresAt"/> set).</summary>
public sealed record DeviceInfo(string Name, DateTime? PairedAt, DateTime? LastSeen, string? Version, DateTime? CodeExpiresAt);
