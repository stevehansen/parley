using System.Text.Json;

namespace Parley.Update;

/// <summary>
/// Knows the newest stable HC.Parley on NuGet. The hub refreshes it at start and twice a day so
/// the web UI and <c>parley status</c> can offer an update without each paying a NuGet round-trip.
/// </summary>
public sealed class UpdateChecker(Func<CancellationToken, Task<string?>>? fetchIndex = null)
{
    public const string PackageId = "HC.Parley";
    private const string IndexUrl = "https://api.nuget.org/v3-flatcontainer/hc.parley/index.json";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    private readonly Func<CancellationToken, Task<string?>> _fetchIndex = fetchIndex ?? FetchIndexAsync;

    public static Version Current { get; } = Normalize(typeof(UpdateChecker).Assembly.GetName().Version);

    // Assembly versions carry a fourth part; NuGet's don't, and 0.1.0.0 > 0.1.0 to System.Version.
    private static Version Normalize(Version? v) => v == null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Newest stable version seen on NuGet, or null before the first successful check.</summary>
    public Version? Latest { get; private set; }

    public bool UpdateAvailable => Latest != null && Latest > Current;

    /// <summary>Checks now; never throws. Returns the newest stable version, or null when NuGet can't say.</summary>
    public async Task<Version?> CheckAsync(CancellationToken ct = default)
    {
        var json = await _fetchIndex(ct);
        if (json == null) return Latest;
        var latest = SelectLatestStable(json);
        if (latest != null) Latest = latest;
        return Latest;
    }

    public async Task RunPeriodicallyAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await CheckAsync(ct);
            try { await Task.Delay(Interval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    internal static Version? SelectLatestStable(string indexJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(indexJson);
            if (!doc.RootElement.TryGetProperty("versions", out var versions)) return null;
            return versions.EnumerateArray()
                .Select(v => v.GetString())
                .Where(v => v != null && !v.Contains('-')) // pre-releases never auto-offered
                .Select(v => Version.TryParse(v, out var parsed) ? Normalize(parsed) : null)
                .Where(v => v != null)
                .Max();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> FetchIndexAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Parley/{Current.ToString(3)}");
            return await http.GetStringAsync(IndexUrl, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}
