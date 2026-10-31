using System.Text;
using System.Text.Json;

namespace SharpClaw.Agent.Tools;

/// <summary>
/// Paged reading of a UTF-8 text file (agent tools spec section 4.6), shared by
/// <c>workspace_read</c> and <c>skill_read</c> so both page exactly the same way.
/// </summary>
internal static class TextPaging
{
    public const int MaxPageBytes = 64 * 1024;

    /// <summary>One page: the text, where it starts, where the next page starts and the file length.</summary>
    public sealed record Page(string Content, long Offset, long NextOffset, long Length)
    {
        public bool Truncated => NextOffset < Length;
    }

    /// <summary>
    /// Reads at most <see cref="MaxPageBytes"/> from <paramref name="requested"/>, starting at the first
    /// UTF-8 character boundary at or after it and ending before a character that wouldn't fit.
    /// </summary>
    public static async Task<Page> ReadPageAsync(Stream stream, long requested, CancellationToken ct)
    {
        var length = stream.Length;
        var start = Math.Min(requested, length);
        stream.Seek(start, SeekOrigin.Begin);

        // Up to 3 extra bytes on each side let the page snap to UTF-8 character boundaries.
        var buffer = new byte[Math.Min(length - start, MaxPageBytes + 6)];
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);

        var skip = 0;
        while (skip < 3 && skip < read && IsContinuation(buffer[skip]))
            skip++;

        var end = Math.Min(read, skip + MaxPageBytes);
        if (end < read)
        {
            var limit = end - 3;
            while (end > skip && end > limit && IsContinuation(buffer[end]))
                end--;
        }

        return new Page(Encoding.UTF8.GetString(buffer, skip, end - skip), start + skip, start + end, length);
    }

    /// <summary>Reads the optional <c>offset</c> argument; false when it is present but not a non-negative integer.</summary>
    public static bool TryOffset(JsonElement args, out long offset)
    {
        offset = 0;
        if (!args.TryGetProperty("offset", out var el) || el.ValueKind == JsonValueKind.Null)
            return true;
        return el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out offset) && offset >= 0;
    }

    private static bool IsContinuation(byte b) => (b & 0b1100_0000) == 0b1000_0000;
}
