using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.GitHub;

namespace SharpClaw.Gateway.Services.Broker;

/// <summary>
/// <c>github_read</c> (external tools spec section 7.6), in the gateway with the operator's token:
/// a fixed set of operations, the optional <c>GitHub:Repos</c> allow-list checked before anything is
/// sent, and answers reduced to the fields the model needs and capped at 64 KB.
/// </summary>
public sealed class GitHubReadToolHandler(GitHubClient github, IOptions<GitHubOptions> options) : IBrokerToolHandler
{
    public const int MaxResultBytes = 64 * 1024;
    private const int MaxBodyChars = 8 * 1024;
    private const int MaxTextChars = 60 * 1024;

    public IReadOnlyCollection<string> Tools { get; } = [BrokerTools.GitHubRead];

    public async Task<BrokerResult> InvokeAsync(BrokerInvocation call, CancellationToken ct)
    {
        var (request, error) = GitHubRequest.Parse(call.Arguments, GitHubOperations.Read);
        if (request is null)
            return BrokerResult.ToolError(ErrorCodes.InvalidArguments, error!);
        if (!options.Value.IsRepoAllowed(request.Owner, request.Repo))
            return GitHubRequest.RepoNotAllowed(request);
        if (!github.HasOwnerToken)
            return BrokerResult.Unavailable("GitHub is not configured on this gateway.");

        var (o, r, n) = (request.Owner, request.Repo, request.Number ?? 0);
        var result = request.Operation switch
        {
            GitHubOperations.GetIssue => Shape(await github.GetIssueAsync(o, r, n, ct), Issue),
            GitHubOperations.ListIssues => Shape(await github.ListIssuesAsync(o, r, request.State, request.Page, ct),
                list => new { issues = list.Select(i => new { i.Number, i.Title, i.State, author = i.User?.Login, labels = Labels(i), isPullRequest = i.PullRequest is not null }), page = request.Page }),
            GitHubOperations.GetPull => Shape(await github.GetPullAsync(o, r, n, ct), Pull),
            GitHubOperations.GetPullDiff => Shape(await github.GetPullDiffAsync(o, r, n, ct), Diff),
            GitHubOperations.ListComments => Shape(await github.ListCommentsAsync(o, r, n, request.Page, ct),
                list => new { comments = list.Select(c => new { author = c.User?.Login, body = Cap(c.Body, MaxBodyChars), c.CreatedAt }), page = request.Page }),
            _ => Shape(await github.GetContentsAsync(o, r, request.Path!, request.Ref, ct), File),
        };
        return result with { TargetHost = "api.github.com", Target = request.Target };
    }

    private static object Issue(GhIssue i) => new
    {
        i.Number, i.Title, i.State, author = i.User?.Login, body = Cap(i.Body, MaxBodyChars), labels = Labels(i),
        i.CreatedAt, url = i.HtmlUrl, isPullRequest = i.PullRequest is not null,
    };

    private static object Pull(GhPull p) => new
    {
        p.Number, p.Title, p.State, author = p.User?.Login, body = Cap(p.Body, MaxBodyChars), p.Merged, p.Draft,
        @base = p.Base.Ref, head = p.Head.Repo is { } repo ? $"{repo.FullName}:{p.Head.Ref}" : p.Head.Ref,
        p.ChangedFiles, p.Additions, p.Deletions, url = p.HtmlUrl,
    };

    private static object Diff(string diff) => new { diff = Cap(diff, MaxTextChars), truncated = diff.Length > MaxTextChars };

    private static object File(JsonElement? contents)
    {
        var element = contents!.Value;
        if (element.ValueKind == JsonValueKind.Array)
        {
            var entries = element.Deserialize<List<GhContent>>(GitHubJson.Options) ?? [];
            return new { directory = true, entries = entries.Select(e => new { e.Name, e.Type, e.Size }) };
        }
        var file = element.Deserialize<GhContent>(GitHubJson.Options)!;
        if (file.Type != "file" || file.Encoding != "base64" || file.Content is null)
            return new { path = file.Path, type = file.Type, size = file.Size, content = (string?)null };
        var bytes = Convert.FromBase64String(file.Content.Replace("\n", ""));
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0))
            return new { path = file.Path, size = file.Size, binary = true };
        var text = Encoding.UTF8.GetString(bytes);
        return new { path = file.Path, size = file.Size, content = Cap(text, MaxTextChars), truncated = text.Length > MaxTextChars };
    }

    private static List<string> Labels(GhIssue i) => i.Labels?.Select(l => l.Name).ToList() ?? [];

    /// <summary>Maps a GitHub answer to a tool result; failures become tool errors the model can see.</summary>
    internal static BrokerResult Shape<T>(GitHubResponse<T> response, Func<T, object> shape)
    {
        if (!response.Ok)
            return Failure(response.Status);
        var result = BrokerResult.Ok(shape(response.Value!));
        return Fit(result);
    }

    internal static BrokerResult Failure(HttpStatusCode status) => (int)status switch
    {
        404 => BrokerResult.ToolError("not_found", "GitHub has no such repository, issue, pull request or file (or the token can't see it)."),
        401 or 403 => BrokerResult.ToolError("github_forbidden", $"GitHub refused the request ({(int)status})."),
        422 => BrokerResult.ToolError("github_rejected", "GitHub rejected the request as invalid (422)."),
        >= 300 and < 400 => BrokerResult.ToolError("github_error", $"GitHub answered with a redirect ({(int)status}), which is never followed."),
        _ => BrokerResult.ToolError("github_error", $"GitHub answered {(int)status}."),
    };

    /// <summary>Keeps a result within 64 KB, dropping list entries from the end if it must.</summary>
    private static BrokerResult Fit(BrokerResult result)
    {
        var json = result.Result!.Value;
        if (Encoding.UTF8.GetByteCount(json.GetRawText()) <= MaxResultBytes)
            return result;
        var fields = json.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        var list = fields.FirstOrDefault(f => f.Value is JsonElement { ValueKind: JsonValueKind.Array });
        if (list.Key is null)
            return BrokerResult.ToolError("too_large", "The answer is larger than 64 KB.");
        var items = ((JsonElement)list.Value!).EnumerateArray().ToList();
        while (items.Count > 0)
        {
            items.RemoveAt(items.Count - 1);
            fields[list.Key] = items;
            fields["truncated"] = true;
            var element = JsonSerializer.SerializeToElement(fields, BrokerResult.Json);
            if (Encoding.UTF8.GetByteCount(element.GetRawText()) <= MaxResultBytes)
                return BrokerResult.Of(element);
        }
        return BrokerResult.ToolError("too_large", "The answer is larger than 64 KB.");
    }

    internal static string? Cap(string? text, int max) => text is null || text.Length <= max ? text : text[..max] + "…";
}

/// <summary>The validated arguments of a GitHub tool call.</summary>
public sealed record GitHubRequest(
    string Operation, string Owner, string Repo, int? Number, string State, int Page, string? Path, string? Ref)
{
    public string Target => Number is { } n ? $"{Owner}/{Repo}#{n}" : $"{Owner}/{Repo}";

    public static BrokerResult RepoNotAllowed(GitHubRequest r) =>
        BrokerResult.Refused(403, "repo_not_allowed", $"{r.Owner}/{r.Repo} is not on this gateway's GitHub allow-list.", "api.github.com", r.Target);

    public static (GitHubRequest? Request, string? Error) Parse(JsonElement args, IReadOnlyList<string> operations)
    {
        var operation = Str(args, "operation");
        if (operation is null || !operations.Contains(operation))
            return (null, $"operation must be one of: {string.Join(", ", operations)}.");
        var owner = Str(args, "owner");
        var repo = Str(args, "repo");
        if (!GitHubOperations.IsOwner(owner) || !GitHubOperations.IsRepo(repo))
            return (null, "owner and repo must be a GitHub owner and repository name.");

        int? number = null;
        if (GitHubOperations.NeedNumber.Contains(operation))
        {
            if (!args.TryGetProperty("number", out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out var value) || value < 1)
                return (null, "number (the issue or pull request number) is required.");
            number = value;
        }

        var state = Str(args, "state") ?? "open";
        if (state is not ("open" or "closed" or "all"))
            return (null, "state must be open, closed or all.");
        var page = 1;
        if (args.TryGetProperty("page", out var p) && p.ValueKind != JsonValueKind.Null &&
            (p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out page) || page is < 1 or > 100))
            return (null, "page must be 1-100.");

        string? path = null, gitRef = null;
        if (operation == GitHubOperations.GetFile)
        {
            path = Str(args, "path")?.Trim('/');
            if (path is not { Length: > 0 and <= 400 } || path.Split('/').Any(s => s is "" or "." or "..") || path.Contains('\\'))
                return (null, "path must be a file path inside the repository.");
            gitRef = Str(args, "ref");
            if (gitRef is not null && (gitRef.Length is 0 or > 200 || gitRef.Contains("..") ||
                    !gitRef.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/')))
                return (null, "ref must be a branch, tag or commit.");
        }
        return (new GitHubRequest(operation, owner!, repo!, number, state, page, path, gitRef), null);
    }

    internal static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
