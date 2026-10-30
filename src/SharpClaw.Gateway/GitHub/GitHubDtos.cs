using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpClaw.Gateway.GitHub;

// Typed views of the GitHub REST responses the tools use. Everything else in a response is ignored;
// the gateway never renders, converts or executes any of it.

public sealed record GhUser(string Login);
public sealed record GhLabel(string Name);
public sealed record GhRepoRef(string FullName);
public sealed record GhRef(string Ref, string? Sha, GhRepoRef? Repo);

public sealed record GhIssue(
    int Number, string Title, string State, GhUser? User, string? Body, List<GhLabel>? Labels,
    DateTime CreatedAt, string? HtmlUrl, JsonElement? PullRequest);

public sealed record GhPull(
    int Number, string Title, string State, GhUser? User, string? Body, bool? Merged, bool? Draft,
    GhRef Base, GhRef Head, int? ChangedFiles, int? Additions, int? Deletions, string? HtmlUrl);

public sealed record GhComment(GhUser? User, string? Body, DateTime CreatedAt, long? Id = null, string? HtmlUrl = null);

/// <summary>A file (<c>type = file</c>) or a directory entry from the contents API.</summary>
public sealed record GhContent(string Type, string Name, string Path, long Size, string? Encoding, string? Content);

public sealed record GhRepository(string FullName, bool Fork, GhRepoRef? Parent, string DefaultBranch);

public sealed record GhSha(string Sha);
public sealed record GhGitCommit(string Sha, GhSha Tree);
public sealed record GhRefObject(string Ref, GhSha Object);

/// <summary>One entry of a new tree; a null <c>Sha</c> deletes the path.</summary>
public sealed record GhTreeEntry(string Path, string Mode, string Type, string? Sha);

internal static class GitHubJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.Strict,
    };
}
