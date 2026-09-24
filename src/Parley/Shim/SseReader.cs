using System.Runtime.CompilerServices;
using System.Text;

namespace Parley.Shim;

/// <summary>Minimal Server-Sent Events parser: yields (event, data) per dispatched event, skipping comments.</summary>
internal static class SseReader
{
    public static async IAsyncEnumerable<(string evt, string data)> ReadAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var evt = "message";
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0) yield return (evt, data.ToString());
                evt = "message";
                data.Clear();
            }
            else if (line.StartsWith("event:"))
            {
                evt = line[6..].Trim();
            }
            else if (line.StartsWith("data:"))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
        }
    }
}
