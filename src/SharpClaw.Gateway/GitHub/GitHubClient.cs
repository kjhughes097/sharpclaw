using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Gateway.GitHub;

/// <summary>A GitHub answer: the parsed value on success, otherwise the HTTP status.</summary>
public sealed record GitHubResponse<T>(T? Value, HttpStatusCode Status)
{
    public bool Ok => Value is not null && (int)Status is >= 200 and < 300;
}

/// <summary>
/// Fixed, typed calls to api.github.com (spec section 7.6). Each token lives only in its own named
/// <see cref="HttpClient"/> whose base address is fixed and which never follows redirects, so a token
/// can't be sent anywhere else. Paths are built only from validated owner/repo names, numbers and
/// escaped path segments: there is no way to ask for an arbitrary URL.
/// </summary>
public sealed class GitHubClient(IHttpClientFactory factory, IOptions<GitHubOptions> options)
{
    /// <summary>The operator's token: reads, writes, opening PRs.</summary>
    public const string OwnerClient = "github-owner";

    /// <summary>The machine user's token: refs and Git Data in its own forks only.</summary>
    public const string ForkClient = "github-fork";

    public const int MaxResponseBytes = 8 * 1024 * 1024;
    private static readonly Uri Base = new(GitHubOptions.ApiBaseUrl);

    public bool HasOwnerToken => options.Value.Token.Length > 0;
    public bool HasForkToken => options.Value.ForkToken.Length > 0;

    /// <summary>Registers the two clients. Each gets only its own token; neither follows redirects.</summary>
    public static void Register(IServiceCollection services)
    {
        foreach (var (name, token) in new (string, Func<GitHubOptions, string>)[] { (OwnerClient, o => o.Token), (ForkClient, o => o.ForkToken) })
        {
            services.AddHttpClient(name, (sp, c) =>
                {
                    c.BaseAddress = Base;
                    c.Timeout = TimeSpan.FromSeconds(30);
                    c.DefaultRequestHeaders.UserAgent.ParseAdd("SharpClaw/3");
                    c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                    c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
                    var value = token(sp.GetRequiredService<IOptions<GitHubOptions>>().Value);
                    if (value.Length > 0)
                        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", value);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
        }
    }

    // ----- Reads (owner token) -----

    public Task<GitHubResponse<GhIssue>> GetIssueAsync(string owner, string repo, int number, CancellationToken ct) =>
        GetJsonAsync<GhIssue>(OwnerClient, $"{Repo(owner, repo)}/issues/{number}", ct);

    public Task<GitHubResponse<List<GhIssue>>> ListIssuesAsync(string owner, string repo, string state, int page, CancellationToken ct) =>
        GetJsonAsync<List<GhIssue>>(OwnerClient, $"{Repo(owner, repo)}/issues?state={state}&per_page=30&page={page}", ct);

    public Task<GitHubResponse<GhPull>> GetPullAsync(string owner, string repo, int number, CancellationToken ct) =>
        GetJsonAsync<GhPull>(OwnerClient, $"{Repo(owner, repo)}/pulls/{number}", ct);

    public Task<GitHubResponse<string>> GetPullDiffAsync(string owner, string repo, int number, CancellationToken ct) =>
        SendAsync<string>(OwnerClient, HttpMethod.Get, $"{Repo(owner, repo)}/pulls/{number}", null, ct, accept: "application/vnd.github.diff");

    public Task<GitHubResponse<List<GhComment>>> ListCommentsAsync(string owner, string repo, int number, int page, CancellationToken ct) =>
        GetJsonAsync<List<GhComment>>(OwnerClient, $"{Repo(owner, repo)}/issues/{number}/comments?per_page=30&page={page}", ct);

    /// <summary>A file, or a directory listing, from the contents API (raw JSON: an object or an array).</summary>
    public Task<GitHubResponse<JsonElement?>> GetContentsAsync(string owner, string repo, string path, string? gitRef, CancellationToken ct)
    {
        var escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        var query = gitRef is null ? "" : $"?ref={Uri.EscapeDataString(gitRef)}";
        return GetJsonAsync<JsonElement?>(OwnerClient, $"{Repo(owner, repo)}/contents/{escaped}{query}", ct);
    }

    // ----- Writes (owner token) -----

    public Task<GitHubResponse<GhComment>> CreateCommentAsync(string owner, string repo, int number, string body, CancellationToken ct) =>
        SendAsync<GhComment>(OwnerClient, HttpMethod.Post, $"{Repo(owner, repo)}/issues/{number}/comments", new { body }, ct);

    public Task<GitHubResponse<GhIssue>> CreateIssueAsync(
        string owner, string repo, string title, string? body, IReadOnlyList<string> labels, CancellationToken ct) =>
        SendAsync<GhIssue>(OwnerClient, HttpMethod.Post, $"{Repo(owner, repo)}/issues", new { title, body, labels }, ct);

    public Task<GitHubResponse<List<GhLabel>>> AddLabelsAsync(string owner, string repo, int number, IReadOnlyList<string> labels, CancellationToken ct) =>
        SendAsync<List<GhLabel>>(OwnerClient, HttpMethod.Post, $"{Repo(owner, repo)}/issues/{number}/labels", new { labels }, ct);

    public Task<GitHubResponse<List<GhLabel>>> RemoveLabelAsync(string owner, string repo, int number, string label, CancellationToken ct) =>
        SendAsync<List<GhLabel>>(OwnerClient, HttpMethod.Delete, $"{Repo(owner, repo)}/issues/{number}/labels/{Uri.EscapeDataString(label)}", null, ct);

    // ----- Plumbing -----

    internal static string Repo(string owner, string repo) =>
        $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";

    private Task<GitHubResponse<T>> GetJsonAsync<T>(string client, string path, CancellationToken ct) =>
        SendAsync<T>(client, HttpMethod.Get, path, null, ct);

    /// <summary>
    /// Sends one request to api.github.com and parses the answer as <typeparamref name="T"/> (a
    /// string reads the body as text). Redirects come back as their 3xx status and are never followed.
    /// </summary>
    internal async Task<GitHubResponse<T>> SendAsync<T>(
        string client, HttpMethod method, string path, object? body, CancellationToken ct, string? accept = null)
    {
        var uri = new Uri(Base, path);
        if (uri.Host != Base.Host || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("GitHub requests go to api.github.com only.");

        using var request = new HttpRequestMessage(method, uri);
        if (accept is not null)
        {
            request.Headers.Accept.Clear();
            request.Headers.Accept.ParseAdd(accept);
        }
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, GitHubJson.Options), Encoding.UTF8, "application/json");

        using var response = await factory.CreateClient(client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            return new GitHubResponse<T>(default, response.StatusCode);
        if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(bool))
            return new GitHubResponse<T>((T)(object)true, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxResponseBytes + 1];
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
        if (read > MaxResponseBytes)
            return new GitHubResponse<T>(default, HttpStatusCode.RequestEntityTooLarge);
        if (typeof(T) == typeof(string))
            return new GitHubResponse<T>((T)(object)Encoding.UTF8.GetString(buffer, 0, read), response.StatusCode);
        try
        {
            return new GitHubResponse<T>(JsonSerializer.Deserialize<T>(buffer.AsSpan(0, read), GitHubJson.Options), response.StatusCode);
        }
        catch (JsonException)
        {
            return new GitHubResponse<T>(default, HttpStatusCode.BadGateway);
        }
    }
}
