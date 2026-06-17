using Microsoft.Extensions.Options;
using SharpClaw.Configuration;

namespace SharpClaw.Audio;

public sealed class WhisperModelDownloader(
    IOptions<SttOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<WhisperModelDownloader> logger)
{
    private const string BaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main";

    private static readonly HashSet<string> ValidSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tiny", "tiny.en", "base", "base.en", "small", "small.en",
        "medium", "medium.en", "large-v1", "large-v2", "large-v3"
    };

    private readonly SttOptions _options = options.Value;

    public string GetModelPath()
    {
        var size = _options.ModelSize;
        if (!ValidSizes.Contains(size))
        {
            throw new InvalidOperationException(
                $"Invalid Stt:ModelSize '{size}'. Valid: {string.Join(", ", ValidSizes)}");
        }

        Directory.CreateDirectory(_options.ModelDirectory);
        return Path.Combine(_options.ModelDirectory, $"ggml-{size}.bin");
    }

    public async Task<string> EnsureModelAsync(CancellationToken ct = default)
    {
        var path = GetModelPath();

        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            return path;
        }

        if (!_options.AutoDownload)
        {
            throw new FileNotFoundException(
                $"Whisper model not found at '{path}' and Stt:AutoDownload is disabled.", path);
        }

        var url = $"{BaseUrl}/ggml-{_options.ModelSize}.bin";
        logger.LogInformation("Downloading whisper model {Size} from {Url} → {Path}",
            _options.ModelSize, url, path);

        var http = httpClientFactory.CreateClient("whisper-models");
        http.Timeout = TimeSpan.FromMinutes(10);

        var tempPath = path + ".part";
        try
        {
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(tempPath);
                await input.CopyToAsync(output, ct);
            }

            File.Move(tempPath, path, overwrite: true);
            logger.LogInformation("Whisper model downloaded: {Size} ({Bytes:N0} bytes)",
                _options.ModelSize, new FileInfo(path).Length);
            return path;
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }
}
