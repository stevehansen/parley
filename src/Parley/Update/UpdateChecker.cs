using System.Text.Json;

namespace Parley.Update;

/// <summary>
/// Knows the newest stable HC.Parley on NuGet. The hub refreshes it at start and every hour so
/// the web UI and <c>parley status</c> can offer an update without each paying a NuGet round-trip.
///
/// It reads the registration index, the one <c>dotnet tool update</c> resolves versions from: a
/// release shows up in the package listing minutes earlier, and offering it then only makes the
/// update fail with "version not found".
/// </summary>
public sealed class UpdateChecker(Func<CancellationToken, Task<string?>>? fetchIndex = null)
{
    public const string PackageId = "HC.Parley";
    private const string IndexUrl = "https://api.nuget.org/v3/registration5-gz-semver2/hc.parley/index.json";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

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

    /// <summary>Newest listed, stable version in a registration index.</summary>
    internal static Version? SelectLatestStable(string registrationJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(registrationJson);
            if (!doc.RootElement.TryGetProperty("items", out var pages) || pages.ValueKind != JsonValueKind.Array) return null;
            return pages.EnumerateArray().SelectMany(ListedVersions)
                .Where(v => v != null && !v.Contains('-')) // pre-releases never auto-offered
                .Select(v => Version.TryParse(v, out var parsed) ? Normalize(parsed) : null)
                .Where(v => v != null)
                .Max();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static IEnumerable<string?> ListedVersions(JsonElement page)
    {
        // Big packages page their versions out; a page without inline items still names its newest.
        if (!page.TryGetProperty("items", out var leaves))
        {
            yield return page.TryGetProperty("upper", out var upper) ? upper.GetString() : null;
            yield break;
        }
        foreach (var leaf in leaves.EnumerateArray())
        {
            var entry = leaf.GetProperty("catalogEntry");
            if (entry.TryGetProperty("listed", out var listed) && listed.ValueKind == JsonValueKind.False) continue;
            yield return entry.GetProperty("version").GetString();
        }
    }

    private static async Task<string?> FetchIndexAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip })
                { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Parley/{Current.ToString(3)}");
            return await http.GetStringAsync(IndexUrl, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}
