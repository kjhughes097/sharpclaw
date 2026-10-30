using SharpClaw.Contracts;

namespace SharpClaw.Gateway.Configuration;

/// <summary>
/// GitHub (external tools spec sections 7.6 and 7.7). Two fine-grained tokens, each used only by its
/// own HTTP client fixed to api.github.com: the operator's (<c>GITHUB_TOKEN</c>) for reads, writes
/// and opening PRs, and the machine user's (<c>GITHUB_FORK_TOKEN</c>) only to build branches in its
/// forks. Without a token the GitHub tools answer <c>503 tool_unavailable</c>.
/// </summary>
public sealed class GitHubOptions : IHasProblem
{
    public const string Section = "GitHub";
    public const string ApiBaseUrl = "https://api.github.com/";

    public string Token { get; set; } = "";
    public string ForkToken { get; set; } = "";

    /// <summary>The machine user that owns the forks (e.g. <c>sharpclaw-bot</c>).</summary>
    public string ForkOwner { get; set; } = "";

    /// <summary>
    /// <c>owner/repo</c> allow-list. Reads and writes: optional (empty allows any repository the
    /// token can see). <c>github_create_pr</c>: required; the clone's origin must be listed.
    /// </summary>
    public string[] Repos { get; set; } = [];

    /// <summary>Paths a PR may never add, change or delete. Null (unset) means <c>.github/workflows/**</c>.</summary>
    public string[]? BlockedPaths { get; set; }

    public static readonly string[] DefaultBlockedPaths = [".github/workflows/**"];

    public IReadOnlyList<string> BlockedPathPatterns =>
        (BlockedPaths ?? DefaultBlockedPaths).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

    public bool IsRepoAllowed(string owner, string repo, bool requireListed = false) =>
        Repos.Length == 0
            ? !requireListed
            : Repos.Any(r => string.Equals(r.Trim(), $"{owner}/{repo}", StringComparison.OrdinalIgnoreCase));

    public string? Problem()
    {
        foreach (var r in Repos)
            if (r.Split('/') is not [var owner, var repo] || !GitHubOperations.IsOwner(owner) || !GitHubOperations.IsRepo(repo))
                return $"GitHub:Repos entry '{r}' must be owner/repo.";
        if (ForkOwner.Length > 0 && !GitHubOperations.IsOwner(ForkOwner))
            return "GitHub:ForkOwner must be a GitHub user name.";
        if (Token.Length > 0 && Token == ForkToken)
            return "GitHub:Token and GitHub:ForkToken must be different tokens.";
        return null;
    }
}
