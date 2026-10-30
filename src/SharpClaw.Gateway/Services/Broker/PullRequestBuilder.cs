using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Auth;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.GitHub;

namespace SharpClaw.Gateway.Services.Broker;

/// <summary>
/// <c>github_create_pr</c> (external tools spec section 7.7): opens a PR on the upstream repository
/// from a branch in the machine user's fork. <see cref="PlanAsync"/> gathers and validates everything
/// (the clone record, the change set, the hardline rules) and its hash goes into the approval, so the
/// files can't change between approval and creation. <see cref="OpenAsync"/> then builds the branch
/// with the fork token (never pushing, never moving an existing ref) and opens the PR with the
/// operator's token. Neither token ever enters a sandbox.
/// </summary>
public sealed class PullRequestBuilder(
    SandboxClient sandbox, SharpClawDbContext db, GitHubClient github, IOptions<GitHubOptions> gitHubOptions)
{
    public const int MaxTitleChars = 256;
    public const int MaxBodyChars = 60_000;
    private const int MaxChangeSetResponseBytes = 8 * 1024 * 1024;

    public sealed record ChangedFile(string Path, string Status, string Mode, int Additions, int Deletions, byte[]? Content);

    /// <summary>Everything needed to open the PR, checked; <see cref="Hash"/> covers the arguments and the change set.</summary>
    public sealed record Plan(
        string Owner, string Repo, string ForkOwner, string Branch, string BaseSha, string Title, string Body,
        IReadOnlyList<ChangedFile> Files, string Hash)
    {
        public int Additions => Files.Sum(f => f.Additions);
        public int Deletions => Files.Sum(f => f.Deletions);
        public string Summary => $"{Files.Count} file{(Files.Count == 1 ? "" : "s")}, +{Additions} −{Deletions}";
    }

    public async Task<(Plan? Plan, BrokerResult? Refusal)> PlanAsync(
        AuthenticatedAgent agent, BrokerAgentOptions config, JsonElement args, CancellationToken ct)
    {
        var gh = gitHubOptions.Value;
        var clonePath = GitHubRequest.Str(args, "clonePath")?.Trim().Trim('/');
        var title = GitHubRequest.Str(args, "title")?.Trim();
        var body = GitHubRequest.Str(args, "body") ?? "";
        var branch = GitHubRequest.Str(args, "branch");
        if (clonePath is not { Length: > 0 })
            return Refuse(400, ErrorCodes.InvalidArguments, "clonePath (the clone's workspace folder) is required.");
        if (title is not { Length: > 0 and <= MaxTitleChars })
            return Refuse(400, ErrorCodes.InvalidArguments, $"title must be 1-{MaxTitleChars} characters.");
        if (body.Length > MaxBodyChars)
            return Refuse(400, ErrorCodes.InvalidArguments, $"body must be at most {MaxBodyChars} characters.");
        if (!IsBranch(branch) || !IsBranch(agent.Name.ToLowerInvariant()))
            return Refuse(403, "invalid_branch", "branch must match [a-z0-9][a-z0-9._/-]{0,60} with no '..', '//' or trailing '/' or '.lock'.");
        if (!github.HasOwnerToken || !github.HasForkToken || gh.ForkOwner.Length == 0)
            return (null, BrokerResult.Unavailable("Pull requests need GitHub:Token, GitHub:ForkToken and GitHub:ForkOwner."));

        // The origin and base commit come from the gateway's own record, never from the sandbox.
        var normalised = clonePath.Replace('\\', '/');
        var clone = await db.SandboxClones.AsNoTracking()
            .FirstOrDefaultAsync(c => c.AgentId == agent.Id && c.Path == normalised, ct);
        if (clone is null)
            return Refuse(404, ErrorCodes.NotFound, $"No clone made with git_clone at '{normalised}'.");
        var origin = clone.Repo.Split('/');
        if (clone.Host != "github.com" || origin.Length != 2 || !gh.IsRepoAllowed(origin[0], origin[1], requireListed: true))
            return Refuse(403, "repo_not_allowed", $"{clone.Host}/{clone.Repo} is not on this gateway's GitHub:Repos list.");

        var changes = await sandbox.CallAsync(config, "git_changes",
            JsonSerializer.SerializeToElement(new { path = clone.Path, @base = clone.BaseSha }),
            TimeSpan.FromSeconds(90), "api.github.com", ct, MaxChangeSetResponseBytes);
        if (changes.Result is not { } result)
            return (null, changes);
        if (!result.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            return (null, changes); // the runner's own error (too many files, not a clone…) goes back as it is

        var files = (result.GetProperty("files").Deserialize<List<WireFile>>(BrokerResult.Json) ?? [])
            .Select(f => new ChangedFile(f.Path, f.Status, f.Mode, f.Additions, f.Deletions, f.Content is null ? null : Convert.FromBase64String(f.Content)))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
        if (Validate(files, gh.BlockedPathPatterns) is { } refusal)
            return (null, refusal);

        var fullBranch = $"sharpclaw/{agent.Name.ToLowerInvariant()}/{branch}";
        var hash = ToolArgsHash.Combine(ToolArgsHash.Compute(args), ChangeSetHash(files));
        return (new Plan(origin[0], origin[1], gh.ForkOwner, fullBranch, clone.BaseSha, title, body, files, hash), null);
    }

    /// <summary>The approval detail: target, branch and every file with its line counts, never contents.</summary>
    public static string Detail(Plan plan)
    {
        var sb = new StringBuilder();
        sb.Append($"PR to {plan.Owner}/{plan.Repo} from {plan.ForkOwner}:{plan.Branch}\n");
        sb.Append($"title: {ApprovalDetails.Cap(plan.Title, 300)}\n");
        sb.Append($"{plan.Summary}\n");
        for (var i = 0; i < plan.Files.Count; i++)
        {
            var f = plan.Files[i];
            var line = $"  {f.Status[0..1].ToUpperInvariant()} {f.Path} (+{f.Additions} −{f.Deletions}){(f.Mode == ChangeSetLimits.ModeExecutable ? " [executable]" : "")}\n";
            if (sb.Length + line.Length > ApprovalDetailLimits.MaxChars - 40)
            {
                sb.Append($"  … and {plan.Files.Count - i} more\n");
                break;
            }
            sb.Append(line);
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Builds the branch in the fork and opens the PR (spec section 7.7, steps 4-6).</summary>
    public async Task<BrokerResult> OpenAsync(Plan plan, string agentName, CancellationToken ct)
    {
        var target = $"{plan.Owner}/{plan.Repo}";

        // The fork must be the machine user's fork of exactly this repository.
        var fork = await github.GetRepositoryAsync(GitHubClient.ForkClient, plan.ForkOwner, plan.Repo, ct);
        if (!fork.Ok || !fork.Value!.Fork || !string.Equals(fork.Value.Parent?.FullName, target, StringComparison.OrdinalIgnoreCase))
            return Fail(403, "fork_missing", $"{plan.ForkOwner}/{plan.Repo} must be a fork of {target} (set it up once by hand).", target);
        var upstream = await github.GetRepositoryAsync(GitHubClient.OwnerClient, plan.Owner, plan.Repo, ct);
        if (!upstream.Ok)
            return GitHubReadToolHandler.Failure(upstream.Status) with { TargetHost = "api.github.com", Target = target };

        // Never reuse, update or force a branch that already exists.
        var existing = await github.GetForkRefAsync(plan.Repo, plan.Branch, ct);
        if (existing.Ok)
            return Fail(409, "branch_exists", $"{plan.ForkOwner}:{plan.Branch} already exists; choose another branch name.", target);
        if (existing.Status != HttpStatusCode.NotFound)
            return GitHubReadToolHandler.Failure(existing.Status) with { TargetHost = "api.github.com", Target = target };

        // A ref at the base commit; if the fork can't see that commit yet, sync it once and retry.
        var created = await github.CreateForkRefAsync(plan.Repo, plan.Branch, plan.BaseSha, ct);
        if (!created.Ok)
        {
            await github.MergeUpstreamAsync(plan.Repo, fork.Value.DefaultBranch, ct);
            created = await github.CreateForkRefAsync(plan.Repo, plan.Branch, plan.BaseSha, ct);
            if (!created.Ok)
                return Fail(409, "base_not_found", $"The fork can't see the clone's base commit {plan.BaseSha[..12]}.", target);
        }

        var baseCommit = await github.GetForkCommitAsync(plan.Repo, plan.BaseSha, ct);
        if (!baseCommit.Ok)
            return Fail(502, "github_error", "Could not read the base commit in the fork.", target);
        var entries = new List<GhTreeEntry>();
        foreach (var file in plan.Files)
        {
            if (file.Status == "deleted")
            {
                entries.Add(new GhTreeEntry(file.Path, ChangeSetLimits.ModeFile, "blob", null));
                continue;
            }
            var blob = await github.CreateBlobAsync(plan.Repo, file.Content ?? [], ct);
            if (!blob.Ok)
                return Fail(502, "github_error", $"Could not upload {file.Path}.", target);
            entries.Add(new GhTreeEntry(file.Path, file.Mode, "blob", blob.Value!.Sha));
        }
        var tree = await github.CreateTreeAsync(plan.Repo, baseCommit.Value!.Tree.Sha, entries, ct);
        var commit = tree.Ok ? await github.CreateCommitAsync(plan.Repo, plan.Title, tree.Value!.Sha, plan.BaseSha, ct) : null;
        if (commit is not { Ok: true })
            return Fail(502, "github_error", "Could not create the commit in the fork.", target);
        var moved = await github.FastForwardForkRefAsync(plan.Repo, plan.Branch, commit.Value!.Sha, ct);
        if (!moved.Ok)
            return Fail(502, "github_error", "Could not move the new branch to the commit.", target);

        var body = $"{plan.Body}\n\n---\n_Opened by the SharpClaw agent `{agentName}`._".TrimStart();
        var pull = await github.CreatePullAsync(plan.Owner, plan.Repo, plan.Title, body,
            $"{plan.ForkOwner}:{plan.Branch}", upstream.Value!.DefaultBranch, ct);
        if (!pull.Ok)
            return GitHubReadToolHandler.Failure(pull.Status) with { TargetHost = "api.github.com", Target = target };

        return BrokerResult.Ok(new
        {
            url = pull.Value!.HtmlUrl,
            number = pull.Value.Number,
            branch = $"{plan.ForkOwner}:{plan.Branch}",
            files = plan.Files.Count,
            additions = plan.Additions,
            deletions = plan.Deletions,
        }, "api.github.com", $"{target}#{pull.Value.Number} ({plan.Summary})");
    }

    /// <summary>The hardline rules (spec section 7.7, step 2): refused outright, never offered for approval.</summary>
    internal static BrokerResult? Validate(IReadOnlyList<ChangedFile> files, IReadOnlyList<string> blockedPaths)
    {
        if (files.Count == 0)
            return Refuse(403, "no_changes", "The clone has no changes to propose.").Refusal;
        if (files.Count > ChangeSetLimits.MaxFiles)
            return Refuse(403, "too_many_files", $"At most {ChangeSetLimits.MaxFiles} files per PR.").Refusal;
        long total = 0;
        foreach (var f in files)
        {
            if (f.Path.Length == 0 || f.Path.StartsWith('/') || f.Path.Contains('\\') || f.Path.Split('/').Any(s => s is "" or "." or ".."))
                return Refuse(403, "invalid_path", $"'{f.Path}' is not a plain repository path.").Refusal;
            if (blockedPaths.FirstOrDefault(p => PathGlob.IsMatch(p, f.Path)) is { } pattern)
                return Refuse(403, "blocked_path", $"{f.Path} matches the blocked pattern {pattern}; PRs may not touch it.").Refusal;
            if (f.Mode == ChangeSetLimits.ModeSymlink)
                return Refuse(403, "symlink_not_allowed", $"{f.Path} is a symbolic link.").Refusal;
            if (f.Mode == ChangeSetLimits.ModeSubmodule)
                return Refuse(403, "submodule_not_allowed", $"{f.Path} is a submodule or nested repository.").Refusal;
            if (f.Mode is not (ChangeSetLimits.ModeFile or ChangeSetLimits.ModeExecutable))
                return Refuse(403, "invalid_mode", $"{f.Path} has an unsupported mode {f.Mode}.").Refusal;
            if (f.Status != "deleted" && f.Content is null)
                return Refuse(403, "invalid_change", $"{f.Path} has no content.").Refusal;
            var size = f.Content?.Length ?? 0;
            if (size > ChangeSetLimits.MaxFileBytes)
                return Refuse(403, "file_too_large", $"{f.Path} is larger than {ChangeSetLimits.MaxFileBytes / 1024} KB.").Refusal;
            total += size;
        }
        return total > ChangeSetLimits.MaxTotalBytes
            ? Refuse(403, "change_set_too_large", $"The changes are larger than {ChangeSetLimits.MaxTotalBytes / (1024 * 1024)} MB.").Refusal
            : null;
    }

    /// <summary>SHA-256 over every file's path, status, mode and content hash, in path order.</summary>
    internal static string ChangeSetHash(IEnumerable<ChangedFile> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files.OrderBy(f => f.Path, StringComparer.Ordinal))
            sb.Append(f.Path).Append('\0').Append(f.Status).Append('\0').Append(f.Mode).Append('\0')
              .Append(f.Content is null ? "-" : Convert.ToHexStringLower(SHA256.HashData(f.Content))).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary><c>[a-z0-9][a-z0-9._/-]{0,60}</c>, and nothing git would refuse as a ref.</summary>
    internal static bool IsBranch(string? branch) =>
        branch is { Length: > 0 and <= 61 } &&
        (char.IsAsciiLetterLower(branch[0]) || char.IsAsciiDigit(branch[0])) &&
        branch.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '/' or '-') &&
        !branch.Contains("..") && !branch.Contains("//") && !branch.EndsWith('/') && !branch.EndsWith(".lock") &&
        !branch.Split('/').Any(s => s.StartsWith('.'));

    private static (Plan?, BrokerResult? Refusal) Refuse(int status, string code, string message) =>
        (null, status == 400 ? BrokerResult.ToolError(code, message) : BrokerResult.Refused(status, code, message));

    private static BrokerResult Fail(int status, string code, string message, string target) =>
        BrokerResult.Refused(status, code, message, "api.github.com", target);

    private sealed record WireFile(string Path, string Status, string Mode, int Additions, int Deletions, string? Content);
}

/// <summary>Case-insensitive globs over '/'-separated paths: <c>*</c> within a segment, <c>**</c> across segments.</summary>
public static class PathGlob
{
    public static bool IsMatch(string pattern, string path)
    {
        var p = pattern.Trim('/').Split('/');
        var s = path.Trim('/').Split('/');
        var memo = new bool?[p.Length + 1, s.Length + 1];
        bool Match(int i, int j)
        {
            if (memo[i, j] is { } known)
                return known;
            bool result;
            if (i == p.Length)
                result = j == s.Length;
            else if (p[i] == "**")
                result = Match(i + 1, j) || (j < s.Length && Match(i, j + 1));
            else
                result = j < s.Length && Segment(p[i], s[j]) && Match(i + 1, j + 1);
            memo[i, j] = result;
            return result;
        }
        return Match(0, 0);
    }

    private static bool Segment(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] != '*' && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }
}
