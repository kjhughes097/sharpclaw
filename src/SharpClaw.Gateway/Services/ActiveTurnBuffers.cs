using System.Collections.Concurrent;
using System.Text;

namespace SharpClaw.Gateway.Services;

/// <summary>
/// The text streamed so far for each in-flight turn, so a reconnecting client can be sent a
/// <c>snapshot</c>. In memory only: a restart loses in-flight turns anyway.
/// </summary>
public sealed class ActiveTurnBuffers(TimeProvider time, ILogger<ActiveTurnBuffers> logger)
{
    public const int MaxBytes = 256 * 1024;
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    private sealed class Buffer(Guid threadId, DateTime startedAt)
    {
        public Guid ThreadId { get; } = threadId;
        public DateTime StartedAt { get; } = startedAt;
        public StringBuilder Text { get; } = new();
        public int Bytes { get; set; }
        public bool CapLogged { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, Buffer> _buffers = new();

    /// <summary>Appends a delta; returns the buffered length after it, or null if the buffer is full.</summary>
    public int? Append(Guid threadId, Guid correlationId, string text)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var buffer = _buffers.GetOrAdd(correlationId, _ => new Buffer(threadId, now));
        lock (buffer)
        {
            var bytes = Encoding.UTF8.GetByteCount(text);
            if (buffer.Bytes + bytes > MaxBytes)
            {
                if (!buffer.CapLogged)
                {
                    buffer.CapLogged = true;
                    logger.LogWarning("active turn buffer full: max_bytes={MaxBytes}", MaxBytes);
                }
                return null;
            }
            buffer.Text.Append(text);
            buffer.Bytes += bytes;
            return buffer.Text.Length;
        }
    }

    public void Remove(Guid correlationId) => _buffers.TryRemove(correlationId, out _);

    /// <summary>In-flight turns for one thread, or for all threads when <paramref name="threadId"/> is null.</summary>
    public IReadOnlyList<(Guid CorrelationId, Guid ThreadId, string Content)> Snapshots(Guid? threadId)
    {
        var cutoff = time.GetUtcNow().UtcDateTime - MaxAge;
        var result = new List<(Guid, Guid, string)>();
        foreach (var (corr, buffer) in _buffers)
        {
            if (buffer.StartedAt < cutoff)
            {
                _buffers.TryRemove(corr, out _);
                continue;
            }
            if (threadId is null || buffer.ThreadId == threadId)
                lock (buffer)
                    result.Add((corr, buffer.ThreadId, buffer.Text.ToString()));
        }
        return result;
    }
}
