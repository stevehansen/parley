using System.IO.Pipelines;
using System.Text;
using Parley.Shim;

namespace Parley.Tests;

public class SseReaderTests
{
    [Fact]
    public async Task SilentStream_TimesOut()
    {
        var pipe = new Pipe(); // never written: a connection that died without a FIN
        await Should.ThrowAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in SseReader.ReadAsync(pipe.Reader.AsStream(), TimeSpan.FromMilliseconds(200), default)) { }
        });
    }

    [Fact]
    public async Task Heartbeats_KeepTheStreamAlive()
    {
        var pipe = new Pipe();
        // Longer overall than the idle limit, with gaps far below it: slow CI runners stretch delays.
        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 30; i++)
            {
                await Write(pipe, ": heartbeat\n\n");
                await Task.Delay(50);
            }
            await Write(pipe, "event: message\ndata: hello\n\n");
            await pipe.Writer.CompleteAsync();
        });

        var events = new List<(string evt, string data)>();
        await foreach (var e in SseReader.ReadAsync(pipe.Reader.AsStream(), TimeSpan.FromSeconds(1), default))
            events.Add(e);
        await writer;
        events.ShouldBe([("message", "hello")]);
    }

    [Fact]
    public async Task CallerCancellation_IsNotATimeout()
    {
        var pipe = new Pipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var ex = await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in SseReader.ReadAsync(pipe.Reader.AsStream(), TimeSpan.FromMinutes(1), cts.Token)) { }
        });
        ex.ShouldNotBeOfType<TimeoutException>();
    }

    private static async Task Write(Pipe pipe, string text)
    {
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(text));
        await pipe.Writer.FlushAsync();
    }
}
