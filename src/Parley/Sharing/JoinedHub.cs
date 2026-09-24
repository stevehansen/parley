using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parley.Sharing;

/// <summary>
/// On a device that joined another machine's hub (<c>parley join</c>): where that hub is and this
/// device's token, in remote.json next to where local state would live. Present = joined.
/// </summary>
internal sealed class JoinedHub
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    /// <summary>The name the hub knows this device by.</summary>
    [JsonPropertyName("device")]
    public string Device { get; set; } = "";

    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string FilePath =>
        Path.Combine(Path.GetDirectoryName(ParleyConfig.StateFile)!, "remote.json");

    public static JoinedHub? Load()
    {
        try
        {
            var joined = JsonSerializer.Deserialize<JoinedHub>(File.ReadAllText(FilePath), FileJson);
            return joined is { Url.Length: > 0, Token.Length: > 0 } ? joined : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save()
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Created empty and locked down before the token goes in (%APPDATA% is already per-user on Windows).
        File.WriteAllText(path, "");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(path, JsonSerializer.Serialize(this, FileJson));
    }

    public static bool Delete()
    {
        if (!File.Exists(FilePath)) return false;
        File.Delete(FilePath);
        return true;
    }
}
