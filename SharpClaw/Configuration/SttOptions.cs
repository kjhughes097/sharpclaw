namespace SharpClaw.Configuration;

public sealed class SttOptions
{
    public const string SectionName = "Stt";

    public bool Enabled { get; set; }

    public string ModelSize { get; set; } = "base";

    public string ModelDirectory { get; set; } = "models/whisper";

    public bool AutoDownload { get; set; } = true;

    public string Language { get; set; } = "auto";

    public int MaxConcurrency { get; set; } = 1;

    public string FfmpegPath { get; set; } = "ffmpeg";

    public int MaxAudioBytes { get; set; } = 25 * 1024 * 1024;
}
