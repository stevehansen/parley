using System.Text.Json;

namespace Parley.Update;

/// <summary>
/// Which versions this machine updated from and to (update-history.jsonl next to the state), so
/// <c>parley update --rollback</c> can go back to the exact version that ran before, not merely
/// the release below the current one: an update may have skipped versions.
/// </summary>
internal static class UpdateHistory
{
    internal sealed record Entry(DateTime At, string From, string To, bool Rollback);

    private static string FilePath => Path.Combine(Path.GetDirectoryName(ParleyConfig.StateFile)!, "update-history.jsonl");

    public static void Append(Version from, Version to, bool rollback)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var entry = new Entry(DateTime.UtcNow, from.ToString(3), to.ToString(3), rollback);
        File.AppendAllText(FilePath, JsonSerializer.Serialize(entry, Mcp.Json.Options) + "\n");
    }

    /// <summary>The version that ran before <paramref name="current"/> was installed, if recorded.</summary>
    public static Version? PredecessorOf(Version current) => PredecessorOf(Read(), current);

    /// <remarks>
    /// Only updates count, not rollbacks: rolling back twice steps back twice (0.3 → 0.2 → 0.1)
    /// instead of undoing the first rollback.
    /// </remarks>
    internal static Version? PredecessorOf(IEnumerable<Entry> entries, Version current) =>
        entries.LastOrDefault(e => !e.Rollback && Version.TryParse(e.To, out var to) && to == current) is { } hit
        && Version.TryParse(hit.From, out var from) ? from : null;

    private static List<Entry> Read()
    {
        if (!File.Exists(FilePath)) return [];
        var entries = new List<Entry>();
        foreach (var line in File.ReadLines(FilePath))
        {
            try
            {
                if (JsonSerializer.Deserialize<Entry>(line, Mcp.Json.Options) is { } e) entries.Add(e);
            }
            catch (JsonException)
            {
                // A torn or hand-edited line: skip it.
            }
        }
        return entries;
    }
}
