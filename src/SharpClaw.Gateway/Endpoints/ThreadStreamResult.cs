using System.Text;
using System.Threading.Channels;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Endpoints;

/// <summary>Streams a thread's events as SSE with a 15-second keepalive comment.</summary>
public sealed class ThreadStreamResult : IResult
{
    private static readonly TimeSpan Keepalive = TimeSpan.FromSeconds(15);

    private readonly Guid _threadId;
    private readonly IThreadEventBus _bus;

    public ThreadStreamResult(Guid threadId, IThreadEventBus bus)
    {
        _threadId = threadId;
        _bus = bus;
    }

    public async Task ExecuteAsync(HttpContext http)
    {
        var response = http.Response;
        response.StatusCode = 200;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        var ct = http.RequestAborted;

        using var subscription = _bus.Subscribe(_threadId, out ChannelReader<ThreadSseEvent> reader);

        // Flush headers immediately so clients can open the stream before posting.
        await WriteAsync(response, ": connected\n\n", ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idleCts.CancelAfter(Keepalive);
                try
                {
                    var evt = await reader.ReadAsync(idleCts.Token);
                    await WriteAsync(response, $"event: {evt.Event}\ndata: {evt.DataJson}\n\n", ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await WriteAsync(response, ": keepalive\n\n", ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected.
        }
        catch (ChannelClosedException)
        {
            // Subscription ended.
        }
    }

    private static async Task WriteAsync(HttpResponse response, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }
}
