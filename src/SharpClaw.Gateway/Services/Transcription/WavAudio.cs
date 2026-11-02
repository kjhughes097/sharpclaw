using System.Buffers.Binary;
using System.Text;

namespace SharpClaw.Gateway.Services.Transcription;

/// <summary>
/// Reads the one audio format the gateway accepts: a RIFF/WAVE file of 16 kHz, mono, 16-bit PCM,
/// which is what whisper expects. The browser converts its recording to this before upload, so the
/// gateway needs no audio codecs (voice spec section 2).
/// </summary>
public static class WavAudio
{
    public const int SampleRate = 16000;
    public const int BytesPerSecond = SampleRate * 2;
    private const ushort PcmFormat = 1;

    /// <summary>Parsed samples in [-1, 1], or the reason the bytes aren't acceptable.</summary>
    public readonly record struct Result(float[]? Samples, string? Error)
    {
        public double Seconds => Samples is null ? 0 : (double)Samples.Length / SampleRate;
    }

    public static Result Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !Is(bytes[..4], "RIFF") || !Is(bytes[8..12], "WAVE"))
            return Fail("The audio must be a WAV file.");

        var formatSeen = false;
        var pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = bytes.Slice(pos, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(pos + 4, 4));
            var body = pos + 8;
            if (size > (uint)(bytes.Length - body))
                return Fail("The WAV file is truncated.");

            if (Is(id, "fmt "))
            {
                if (size < 16)
                    return Fail("The WAV format chunk is malformed.");
                var fmt = bytes.Slice(body, 16);
                var format = BinaryPrimitives.ReadUInt16LittleEndian(fmt[..2]);
                var channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..4]);
                var rate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..8]);
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..16]);
                if (format != PcmFormat || channels != 1 || rate != SampleRate || bits != 16)
                    return Fail("The audio must be 16 kHz, mono, 16-bit PCM.");
                formatSeen = true;
            }
            else if (Is(id, "data"))
            {
                if (!formatSeen)
                    return Fail("The WAV file has no format chunk before its data.");
                var data = bytes.Slice(body, (int)size & ~1);
                if (data.Length == 0)
                    return Fail("The recording is empty.");
                var samples = new float[data.Length / 2];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2)) / 32768f;
                return new Result(samples, null);
            }

            // Chunks are padded to an even length.
            pos = body + (int)size + (int)(size & 1);
        }
        return Fail("The WAV file has no audio data.");
    }

    private static bool Is(ReadOnlySpan<byte> id, string expected) => Encoding.ASCII.GetString(id) == expected;

    private static Result Fail(string error) => new(null, error);
}
