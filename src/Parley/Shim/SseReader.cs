using System.Runtime.CompilerServices;
using System.Text;

namespace Parley.Shim;

/// <summary>Minimal Server-Sent Events parser: yields (event, data) per dispatched event, skipping comments.</summary>
internal static class SseReader
{
    /// <param name="idleTimeout">
    /// Longest silence tolerated, heartbeat comments included, before a <see cref="TimeoutException"/>.
    /// A connection that died without a FIN (sleep, network change) never errors on its own: reads
    /// just wait forever.
    /// </param>
    public static async IAsyncEnumerable<(string evt, string data)> ReadAsync(
        Stream stream, TimeSpan idleTimeout, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var evt = "message";
        var data = new StringBuilder();
        while (true)
        {
            string? line;
            idle.CancelAfter(idleTimeout);
            try
            {
                line = await reader.ReadLineAsync(idle.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"No data from the event stream for {idleTimeout.TotalSeconds:0.#} s");
            }
            if (line == null) yield break;

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
