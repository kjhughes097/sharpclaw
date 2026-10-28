using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Endpoints;

/// <summary>
/// An SSE stream of one thread's events, or of every thread's (null thread id), with
/// <c>Last-Event-ID</c> replay (pre-3.3 spec section 8.4) and a 15-second keepalive comment.
/// </summary>
public sealed class EventStreamResult(Guid? threadId, long? lastEventId) : IResult
{
    private static readonly TimeSpan Keepalive = TimeSpan.FromSeconds(15);

    public async Task ExecuteAsync(HttpContext http)
    {
        var sp = http.RequestServices;
        var bus = sp.GetRequiredService<IThreadEventBus>();
        var db = sp.GetRequiredService<SharpClawDbContext>();
        var buffers = sp.GetRequiredService<ActiveTurnBuffers>();
        var ct = http.RequestAborted;

        // 1. Subscribe first so nothing published from here on can be missed.
        using var subscription = bus.Subscribe(threadId, out ChannelReader<ThreadSseEvent> reader);

        // 2–4. Everything up to `baseline` is replayed (or predates the client); newer events
        // come from the bus. Computed before the headers go out, so a client that posts as soon
        // as the stream is open always gets its events live.
        var scoped = threadId is { } t ? db.ThreadEvents.Where(e => e.ThreadId == t) : db.ThreadEvents;
        var baseline = await scoped.MaxAsync(e => (long?)e.Id, ct) ?? 0;
        var prunedThrough = await db.EventLogState.Select(s => s.PrunedThrough).FirstOrDefaultAsync(ct);
        var reset = lastEventId is { } last && last < prunedThrough;
        var replay = lastEventId is { } from && !reset
            ? await scoped.AsNoTracking().Where(e => e.Id > from && e.Id <= baseline).OrderBy(e => e.Id).ToListAsync(ct)
            : new List<ThreadEvent>();

        var response = http.Response;
        response.StatusCode = 200;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            var sb = new StringBuilder("retry: 3000\n\n");
            if (reset)
                Append(sb, SseEventNames.Reset, Json(new ResetEvent(ResetReasons.HistoryPruned, threadId)));
            foreach (var e in replay)
                Append(sb, e.Type, e.Data, e.Id);
            Append(sb, SseEventNames.Ready, Json(new ReadyEvent(threadId)), baseline);

            // 5. The text streamed so far for turns still in flight.
            var snapshotLengths = new Dictionary<Guid, int>();
            foreach (var (corr, turnThread, content) in buffers.Snapshots(threadId))
            {
                snapshotLengths[corr] = content.Length;
                Append(sb, SseEventNames.Snapshot, Json(new SnapshotEvent(corr, content, turnThread)));
            }
            await WriteAsync(response, sb.ToString(), ct);

            // 6. Live, dropping durable events already covered by the replay and deltas already in a snapshot.
            var highestSent = baseline;
            while (!ct.IsCancellationRequested)
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idleCts.CancelAfter(Keepalive);
                try
                {
                    var evt = await reader.ReadAsync(idleCts.Token);
                    if (evt.Id is { } id)
                    {
                        if (id <= highestSent)
                            continue;
                        highestSent = id;
                    }
                    if (evt is { CorrelationId: { } c, EndOffset: { } end } &&
                        snapshotLengths.TryGetValue(c, out var covered) && end <= covered)
                        continue;
                    var line = new StringBuilder();
                    Append(line, evt.Event, evt.DataJson, evt.Id);
                    await WriteAsync(response, line.ToString(), ct);
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

    private static string Json<T>(T payload) => JsonSerializer.Serialize(payload, SharpClawJson.Options);

    private static void Append(StringBuilder sb, string type, string data, long? id = null)
    {
        if (id is not null)
            sb.Append("id: ").Append(id).Append('\n');
        sb.Append("event: ").Append(type).Append("\ndata: ").Append(data).Append("\n\n");
    }

    private static async Task WriteAsync(HttpResponse response, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }

    /// <summary>The client's last durable id: <c>Last-Event-ID</c> header, else <c>?lastEventId=</c>.</summary>
    public static long? ReadLastEventId(HttpRequest request)
    {
        var raw = request.Headers["Last-Event-ID"].ToString();
        if (string.IsNullOrWhiteSpace(raw))
            raw = request.Query["lastEventId"].ToString();
        return long.TryParse(raw, out var id) && id >= 0 ? id : null;
    }
}
