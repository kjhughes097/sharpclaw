using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.GitHub;

namespace SharpClaw.Gateway.Services.Broker;

/// <summary>
/// <c>github_write</c> (external tools spec section 7.6, Q4): comment on an issue or PR, create an
/// issue, add or remove labels, with the operator's token. Always approved by a human (Q2), whose
/// approval detail shows exactly what will be posted. No pushes, merges or settings.
/// </summary>
public sealed class GitHubWriteToolHandler(GitHubClient github, IOptions<GitHubOptions> options) : IBrokerToolHandler
{
    public IReadOnlyCollection<string> Tools { get; } = [BrokerTools.GitHubWrite];

    public async Task<BrokerResult> InvokeAsync(BrokerInvocation call, CancellationToken ct)
    {
        var (request, error) = GitHubRequest.Parse(call.Arguments, GitHubOperations.Write);
        var (write, writeError) = request is null ? (null, null) : GitHubWrite.Parse(request.Operation, call.Arguments);
        if (request is null || write is null)
            return BrokerResult.ToolError(ErrorCodes.InvalidArguments, (error ?? writeError)!);
        if (!options.Value.IsRepoAllowed(request.Owner, request.Repo))
            return GitHubRequest.RepoNotAllowed(request);
        if (!github.HasOwnerToken)
            return BrokerResult.Unavailable("GitHub is not configured on this gateway.");

        var (o, r, n) = (request.Owner, request.Repo, request.Number ?? 0);
        BrokerResult result;
        switch (request.Operation)
        {
            case GitHubOperations.Comment:
                result = GitHubReadToolHandler.Shape(await github.CreateCommentAsync(o, r, n, write.Body!, ct),
                    c => new { commented = true, url = c.HtmlUrl, id = c.Id });
                break;
            case GitHubOperations.CreateIssue:
                var created = await github.CreateIssueAsync(o, r, write.Title!, write.Body, write.Labels, ct);
                result = GitHubReadToolHandler.Shape(created, i => new { created = true, i.Number, url = i.HtmlUrl });
                if (created.Value is { } issue)
                    request = request with { Number = issue.Number };
                break;
            case GitHubOperations.AddLabels:
                result = GitHubReadToolHandler.Shape(await github.AddLabelsAsync(o, r, n, write.Labels, ct),
                    labels => new { labels = labels.Select(l => l.Name) });
                break;
            default:
                // One call per label; a label that isn't on the issue (404) is simply not there.
                var removed = new List<string>();
                foreach (var label in write.Labels)
                {
                    var response = await github.RemoveLabelAsync(o, r, n, label, ct);
                    if (response.Ok)
                        removed.Add(label);
                    else if (response.Status != HttpStatusCode.NotFound)
                        return GitHubReadToolHandler.Failure(response.Status) with { TargetHost = "api.github.com", Target = request.Target };
                }
                result = BrokerResult.Ok(new { removed });
                break;
        }
        return result with { TargetHost = "api.github.com", Target = request.Target };
    }
}

/// <summary>The write-specific arguments: text and labels, validated before anything is sent.</summary>
public sealed record GitHubWrite(string? Title, string? Body, IReadOnlyList<string> Labels)
{
    public const int MaxBodyChars = 65536;
    public const int MaxTitleChars = 256;
    public const int MaxLabels = 10;
    public const int MaxLabelChars = 50;

    public static (GitHubWrite? Write, string? Error) Parse(string operation, JsonElement args)
    {
        var title = GitHubRequest.Str(args, "title");
        var body = GitHubRequest.Str(args, "body");
        var labels = new List<string>();
        if (args.TryGetProperty("labels", out var l) && l.ValueKind != JsonValueKind.Null)
        {
            if (l.ValueKind != JsonValueKind.Array || l.GetArrayLength() > MaxLabels)
                return (null, $"labels must be a list of at most {MaxLabels} names.");
            foreach (var item in l.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= MaxLabelChars } name ||
                    name.Any(char.IsControl))
                    return (null, $"each label must be 1-{MaxLabelChars} characters.");
                labels.Add(name);
            }
        }
        if (body is { Length: > MaxBodyChars })
            return (null, $"body must be at most {MaxBodyChars} characters.");

        return operation switch
        {
            GitHubOperations.Comment when body is not { Length: > 0 } => (null, "body is required."),
            GitHubOperations.CreateIssue when title is not { Length: > 0 and <= MaxTitleChars } => (null, $"title must be 1-{MaxTitleChars} characters."),
            GitHubOperations.AddLabels or GitHubOperations.RemoveLabels when labels.Count == 0 => (null, "labels must name at least one label."),
            _ => (new GitHubWrite(title, body, labels), null),
        };
    }
}
