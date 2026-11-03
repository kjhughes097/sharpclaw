using System.Buffers.Binary;
using System.Text;
using SharpClaw.Gateway.Services.Transcription;

namespace SharpClaw.Gateway.Tests;

/// <summary>The gateway accepts only 16 kHz mono 16-bit PCM WAV (voice spec section 2).</summary>
public class WavAudioTests
{
    /// <summary>A WAV file; <paramref name="extraChunk"/> puts an odd-sized LIST chunk before the data.</summary>
    public static byte[] Wav(short[] samples, int rate = 16000, short channels = 1, short bits = 16,
        short format = 1, bool extraChunk = false)
    {
        using var ms = new MemoryStream();
        void Ascii(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
        void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); ms.Write(b); }

        Ascii("RIFF"); U32(0); Ascii("WAVE");
        Ascii("fmt "); U32(16); U16((ushort)format); U16((ushort)channels); U32((uint)rate);
        U32((uint)(rate * channels * bits / 8)); U16((ushort)(channels * bits / 8)); U16((ushort)bits);
        if (extraChunk)
        {
            Ascii("LIST"); U32(3); ms.Write([1, 2, 3, 0]); // padded to even
        }
        Ascii("data"); U32((uint)(samples.Length * 2));
        foreach (var s in samples) U16((ushort)s);
        return ms.ToArray();
    }

    public static byte[] Silence(double seconds) => Wav(new short[(int)(seconds * WavAudio.SampleRate)]);

    [Fact]
    public void Parses_samples_into_the_unit_range()
    {
        var result = WavAudio.Parse(Wav([0, 16384, -32768, 32767], extraChunk: true));

        Assert.Null(result.Error);
        Assert.Equal([0f, 0.5f, -1f, 32767 / 32768f], result.Samples!);
        Assert.Equal(4.0 / 16000, result.Seconds);
    }

    [Theory]
    [InlineData(44100, 1, 16, 1)]
    [InlineData(16000, 2, 16, 1)]
    [InlineData(16000, 1, 8, 1)]
    [InlineData(16000, 1, 16, 3)] // IEEE float
    public void Rejects_any_other_format(int rate, short channels, short bits, short format)
    {
        var result = WavAudio.Parse(Wav([1, 2], rate, channels, bits, format));
        Assert.Equal("The audio must be 16 kHz, mono, 16-bit PCM.", result.Error);
    }

    [Fact]
    public void Rejects_non_wav_truncated_and_empty_files()
    {
        Assert.Equal("The audio must be a WAV file.", WavAudio.Parse("OggS not a wav"u8).Error);
        Assert.Equal("The audio must be a WAV file.", WavAudio.Parse([]).Error);

        var full = Wav([1, 2, 3]);
        Assert.Equal("The WAV file is truncated.", WavAudio.Parse(full[..^2]).Error);
        Assert.Equal("The recording is empty.", WavAudio.Parse(Wav([])).Error);
        Assert.Equal("The WAV file has no audio data.", WavAudio.Parse(full[..36]).Error);
    }
}
