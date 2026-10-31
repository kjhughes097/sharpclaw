using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SharpClaw.Agent.Tools.Workspace;
using SharpClaw.Contracts;
using SharpClaw.Sandbox.Infrastructure;

namespace SharpClaw.Sandbox.Ops;

/// <summary>GET with redirects followed one hop at a time, each hop re-validated (and re-checked by the egress proxy).</summary>
public sealed class Fetcher(IOutboundHttp http)
{
    public const int MaxRedirects = 5;
    private const string UserAgent = "SharpClaw/3 (agent fetch)";

    public sealed record Fetched(HttpResponseMessage Response, Uri Url) : IDisposable
    {
        public void Dispose() => Response.Dispose();
    }

    /// <summary>The final response, or an error result (bad redirect, too many redirects, unreachable).</summary>
    public async Task<(Fetched? Fetched, OpResult? Error)> GetAsync(Uri url, CancellationToken ct)
    {
        for (var hop = 0; ; hop++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(UserAgent);
                request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.8");
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                // Messages here are ours (proxy refusals) or the runtime's, never page content.
                return (null, OpResult.Error("unreachable", $"Could not fetch {url.Host}: {ex.Message}"));
            }

            var status = (int)response.StatusCode;
            if (status is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location)
                return (new Fetched(response, url), null);

            response.Dispose();
            if (hop == MaxRedirects)
                return (null, OpResult.Error("too_many_redirects", $"More than {MaxRedirects} redirects."));
            var (next, error) = ToolUrls.Parse(new Uri(url, location).ToString());
            if (next is null)
                return (null, OpResult.Error("bad_redirect", $"Redirect refused: {error}."));
            url = next;
        }
    }
}

/// <summary>
/// <c>http_fetch</c> (spec section 7.1): a page as Markdown, plain text or raw. HTML is converted by
/// <c>pandoc --sandbox</c> (no file or network access), with a fixed argv; other text types are
/// returned as they are; binary content is refused. The first 16 KB comes back inline; a longer page
/// is saved whole to <c>savedPath</c> in the workspace for the model to page through (<see cref="Spill"/>).
/// </summary>
public sealed class HttpFetchOp(Fetcher fetcher, IReadOnlyDictionary<string, string> childEnv, WorkspaceRoot workspace, TimeProvider time)
    : IRunnerOp
{
    public const int MaxResultBytes = Spill.InlineBytes;
    public const int MaxBodyBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public string Name => BrokerTools.HttpFetch;

    public async Task<OpResult> RunAsync(JsonElement args, CancellationToken ct)
    {
        var (url, error) = ToolUrls.Parse(OpArgs.String(args, "url"));
        if (url is null)
            return OpResult.Error("invalid_arguments", error!);
        var format = OpArgs.String(args, "format") ?? "markdown";
        if (format is not ("markdown" or "text" or "raw"))
            return OpResult.Error("invalid_arguments", "format must be markdown, text or raw.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var (fetched, fetchError) = await fetcher.GetAsync(url, timeout.Token);
            if (fetched is null)
                return fetchError!;
            using (fetched)
                return await ReadAsync(fetched, format, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return OpResult.Error("timeout", $"The fetch took longer than {Timeout.TotalSeconds:0} s.");
        }
    }

    private async Task<OpResult> ReadAsync(Fetcher.Fetched fetched, string format, CancellationToken ct)
    {
        var response = fetched.Response;
        var status = (int)response.StatusCode;
        if (status >= 400)
            return OpResult.Error("http_error", $"The server answered {status}.", new { url = fetched.Url.ToString(), status });

        var (body, bodyTruncated) = await ReadCappedAsync(response.Content, MaxBodyBytes, ct);
        var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        var isHtml = mediaType is "text/html" or "application/xhtml+xml";
        if (!isHtml && !IsText(mediaType, body))
            return OpResult.Error("binary_content",
                $"The response is {(mediaType.Length > 0 ? mediaType : "binary")}, not text. Use download_to_workspace (and extract_text for PDFs).");

        var text = Decode(body, response.Content.Headers.ContentType);
        if (isHtml && format != "raw")
        {
            var (converted, exitCode) = await ConvertAsync(body, format, ct);
            if (converted is null)
                return OpResult.Error("conversion_failed", $"The page could not be converted (pandoc exit {exitCode}); try format raw.");
            text = converted;
        }

        var (content, truncated) = Truncate(text, MaxResultBytes);
        var bytes = Encoding.UTF8.GetByteCount(text);
        string? savedPath = null;
        if (truncated)
        {
            savedPath = Spill.NewPath("fetch", "fetch", format == "markdown" ? "md" : "txt", time);
            try
            {
                await NoFollow.WriteAtomicAsync(workspace.Path, savedPath, Encoding.UTF8.GetBytes(text), ct);
            }
            catch (WorkspacePathException)
            {
                savedPath = null; // the folder was tampered with: the preview still stands
            }
        }
        return OpResult.Success(new
        {
            url = fetched.Url.ToString(),
            status,
            contentType = mediaType,
            format,
            content,
            truncated = truncated || bodyTruncated,
            bytes,
            savedPath,
        });
    }

    private async Task<(string? Text, int ExitCode)> ConvertAsync(byte[] html, string format, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(new ProcessSpec(
            "pandoc", ["--sandbox", "-f", "html", "-t", format == "markdown" ? "gfm" : "plain", "--wrap=none"],
            Path.GetTempPath(), Timeout, 4 * 1024 * 1024, childEnv, Stdin: html), ct);
        return (result is { ExitCode: 0, TimedOut: false } ? Encoding.UTF8.GetString(result.Stdout) : null, result.ExitCode);
    }

    internal static async Task<(byte[] Body, bool Truncated)> ReadCappedAsync(HttpContent content, int max, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[max + 1];
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
        return read > max ? (buffer[..max], true) : (buffer[..read], false);
    }

    private static bool IsText(string mediaType, byte[] body) =>
        mediaType.StartsWith("text/", StringComparison.Ordinal) ||
        mediaType is "application/json" or "application/xml" or "application/javascript" or "application/x-yaml" or "application/yaml" ||
        mediaType.EndsWith("+json", StringComparison.Ordinal) || mediaType.EndsWith("+xml", StringComparison.Ordinal) ||
        (mediaType.Length == 0 && !body.AsSpan(0, Math.Min(body.Length, 8192)).Contains((byte)0));

    private static string Decode(byte[] body, MediaTypeHeaderValue? type)
    {
        Encoding encoding = Encoding.UTF8;
        try
        {
            if (type?.CharSet is { Length: > 0 } charset)
                encoding = Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            // Unknown charset: UTF-8 is the best guess.
        }
        return encoding.GetString(body);
    }

    /// <summary>Cuts <paramref name="text"/> to at most <paramref name="maxBytes"/> UTF-8 bytes, on a character boundary.</summary>
    internal static (string Text, bool Truncated) Truncate(string text, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
            return (text, false);
        var end = maxBytes;
        while (end > 0 && (bytes[end] & 0b1100_0000) == 0b1000_0000)
            end--;
        return (Encoding.UTF8.GetString(bytes, 0, end), true);
    }
}

/// <summary>
/// <c>download_to_workspace</c> (spec section 7.1): streams a URL to a temporary file in the
/// workspace and renames it into place, at most 50 MB, under the agent's own path rules and
/// without following links.
/// </summary>
public sealed class DownloadOp(Fetcher fetcher, WorkspaceRoot workspace, long maxBytes = DownloadOp.MaxBytes) : IRunnerOp
{
    public const long MaxBytes = 50L * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    public string Name => BrokerTools.DownloadToWorkspace;

    private sealed class TooLargeException : Exception;

    public async Task<OpResult> RunAsync(JsonElement args, CancellationToken ct)
    {
        var (url, error) = ToolUrls.Parse(OpArgs.String(args, "url"));
        if (url is null)
            return OpResult.Error("invalid_arguments", error!);
        var (path, pathError) = workspace.Resolve(OpArgs.String(args, "path"));
        if (path is null)
            return OpResult.Error("invalid_path", pathError!);
        if (OpArgs.Bool(args, "overwrite") is not { } overwrite)
            return OpResult.Error("invalid_arguments", "overwrite must be true or false.");
        if (Directory.Exists(path.FullPath))
            return OpResult.Error("invalid_path", "path is a directory.");
        var existed = File.Exists(path.FullPath);
        if (existed && !overwrite)
            return OpResult.Error("exists", $"'{path.Relative}' already exists; pass overwrite: true to replace it.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var (fetched, fetchError) = await fetcher.GetAsync(url, timeout.Token);
            if (fetched is null)
                return fetchError!;
            using (fetched)
            {
                var status = (int)fetched.Response.StatusCode;
                if (status >= 400)
                    return OpResult.Error("http_error", $"The server answered {status}.", new { url = fetched.Url.ToString(), status });
                if (fetched.Response.Content.Headers.ContentLength > maxBytes)
                    return TooLarge();

                long written = 0;
                await NoFollow.WriteAtomicAsync(workspace.Path, path.Relative, async (file, token) =>
                {
                    await using var body = await fetched.Response.Content.ReadAsStreamAsync(token);
                    var buffer = new byte[81920];
                    int n;
                    while ((n = await body.ReadAsync(buffer, token)) > 0)
                    {
                        written += n;
                        if (written > maxBytes)
                            throw new TooLargeException();
                        await file.WriteAsync(buffer.AsMemory(0, n), token);
                    }
                }, timeout.Token);

                return OpResult.Success(new
                {
                    path = path.Relative,
                    bytes = written,
                    contentType = fetched.Response.Content.Headers.ContentType?.MediaType,
                    created = !existed,
                });
            }
        }
        catch (TooLargeException)
        {
            return TooLarge();
        }
        catch (WorkspacePathException ex)
        {
            return OpResult.Error("invalid_path", ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return OpResult.Error("timeout", $"The download took longer than {Timeout.TotalMinutes:0} minutes.");
        }
    }

    private OpResult TooLarge() =>
        OpResult.Error("too_large", $"The download is larger than {maxBytes / (1024.0 * 1024):0.#} MB.");
}
