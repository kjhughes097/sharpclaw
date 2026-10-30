using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts;
using SharpClaw.Gateway.GitHub;
using SharpClaw.Gateway.Services.Broker;
using static SharpClaw.Gateway.Tests.BrokerTestHelpers;

namespace SharpClaw.Gateway.Tests;

/// <summary>The hardline refusals of <c>github_create_pr</c>: never offered for approval, never sent to GitHub.</summary>
public partial class GitHubCreatePrTests
{
    public static TheoryData<object, string> HardlineFiles => new()
    {
        { File(".github/workflows/ci.yml"), "blocked_path" },
        { File(".GitHub/Workflows/deploy.yml", "added"), "blocked_path" },
        { File(".github/workflows/old.yml", "deleted", text: null), "blocked_path" },
        { File("link", "added", mode: "120000", text: null), "symlink_not_allowed" },
        { File("vendor/lib", "added", mode: "160000", text: null), "submodule_not_allowed" },
        { File("../escape.txt"), "invalid_path" },
        { File("/etc/passwd"), "invalid_path" },
    };

    [Theory]
    [MemberData(nameof(HardlineFiles))]
    public async Task Hardline_change_sets_are_refused_before_approval(object file, string code)
    {
        await using var ctx = await SetupAsync([File("src/ok.cs"), file]);

        var (_, _, resp) = await RequestAsync(ctx.Turn, PrArgs);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(code, await ClientApiHelpers.ErrorCodeAsync(resp));
        Assert.Empty(ctx.GitHub.Requests);
    }

    [Fact]
    public async Task Operators_can_block_more_paths()
    {
        await using var ctx = await SetupAsync([File("deploy/prod.yaml")], new() { ["GitHub:BlockedPaths:0"] = "deploy/**" });

        Assert.Equal("blocked_path", await ClientApiHelpers.ErrorCodeAsync((await RequestAsync(ctx.Turn, PrArgs)).Response));
    }

    [Theory]
    [InlineData("Fix-Bug")]
    [InlineData("-fix")]
    [InlineData("fix..bug")]
    [InlineData("fix//bug")]
    [InlineData("fix/")]
    [InlineData("fix.lock")]
    [InlineData("a-really-long-branch-name-that-goes-on-and-on-past-the-sixty-one-char-limit")]
    public async Task Branch_names_follow_the_rule(string branch)
    {
        await using var ctx = await SetupAsync();

        var resp = (await RequestAsync(ctx.Turn, new { clonePath = "repos/repo", title = "t", branch })).Response;

        Assert.Equal("invalid_branch", await ClientApiHelpers.ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task An_existing_branch_is_never_reused_or_moved()
    {
        await using var ctx = await SetupAsync(extra: new() { ["ToolBroker:AlwaysApprove:0"] = "" });
        ctx.GitHub.On($"GET /repos/{Bot}/repo/git/ref/heads/sharpclaw/assistant/fix-bug", new { @ref = "refs/heads/sharpclaw/assistant/fix-bug", @object = new { sha = "x" } });

        var resp = await ctx.Turn.InvokeAsync(BrokerTools.GitHubCreatePr, PrArgs);

        Assert.Equal("branch_exists", await ClientApiHelpers.ErrorCodeAsync(resp));
        Assert.DoesNotContain(ctx.GitHub.Requests, r => r.Method == HttpMethod.Post || r.Method == HttpMethod.Patch);
    }

    [Theory]
    [InlineData(false, "octo/repo")]
    [InlineData(true, "someone/else")]
    public async Task A_missing_or_unrelated_fork_is_refused(bool isFork, string parent)
    {
        await using var ctx = await SetupAsync(extra: new() { ["ToolBroker:AlwaysApprove:0"] = "" });
        ctx.GitHub.On($"GET /repos/{Bot}/repo", new { full_name = $"{Bot}/repo", fork = isFork, parent = new { full_name = parent }, default_branch = "main" });

        Assert.Equal("fork_missing", await ClientApiHelpers.ErrorCodeAsync(await ctx.Turn.InvokeAsync(BrokerTools.GitHubCreatePr, PrArgs)));
    }

    [Fact]
    public async Task A_base_the_fork_cannot_see_is_synced_once_then_refused()
    {
        await using var ctx = await SetupAsync(extra: new() { ["ToolBroker:AlwaysApprove:0"] = "" });
        ctx.GitHub.On($"POST /repos/{Bot}/repo/git/refs", new { message = "Object does not exist" }, HttpStatusCode.UnprocessableEntity);

        var resp = await ctx.Turn.InvokeAsync(BrokerTools.GitHubCreatePr, PrArgs);

        Assert.Equal("base_not_found", await ClientApiHelpers.ErrorCodeAsync(resp));
        Assert.Single(ctx.GitHub.Requests, r => r.Uri.AbsolutePath.EndsWith("/merge-upstream"));
        Assert.Equal(2, ctx.GitHub.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/git/refs") && r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task Only_repositories_on_the_allow_list_and_known_clones()
    {
        await using var unlisted = await SetupAsync(extra: new() { ["GitHub:Repos:0"] = "octo/other" });
        Assert.Equal("repo_not_allowed", await ClientApiHelpers.ErrorCodeAsync((await RequestAsync(unlisted.Turn, PrArgs)).Response));

        await using var unknown = await SetupAsync(record: false);
        Assert.Equal(HttpStatusCode.NotFound, (await RequestAsync(unknown.Turn, PrArgs)).Response.StatusCode);
    }

    [Fact]
    public async Task Without_the_fork_setup_prs_are_unavailable()
    {
        await using var ctx = await SetupAsync(extra: new() { ["GitHub:ForkOwner"] = "" });

        Assert.Equal(ErrorCodes.ToolUnavailable, await ClientApiHelpers.ErrorCodeAsync((await RequestAsync(ctx.Turn, PrArgs)).Response));
    }

    [Fact]
    public async Task The_fork_token_is_never_used_outside_the_fork_owners_repositories()
    {
        await using var ctx = await SetupAsync();
        var github = ctx.Fx.App.Services.GetRequiredService<GitHubClient>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            github.SendAsync<bool>(GitHubClient.ForkClient, HttpMethod.Get, "repos/octo/repo/git/refs", null, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            github.SendAsync<bool>(GitHubClient.ForkClient, HttpMethod.Get, $"repos/{Bot}-evil/repo", null, default));
        Assert.Empty(ctx.GitHub.Requests);
    }

    [Fact]
    public void Caps_on_files_and_sizes()
    {
        var file = new PullRequestBuilder.ChangedFile("a.txt", "added", "100644", 1, 0, new byte[10]);
        var many = Enumerable.Range(0, ChangeSetLimits.MaxFiles + 1).Select(i => file with { Path = $"f{i}" }).ToList();
        var big = file with { Content = new byte[ChangeSetLimits.MaxFileBytes + 1] };
        var total = Enumerable.Range(0, 6).Select(i => file with { Path = $"f{i}", Content = new byte[ChangeSetLimits.MaxFileBytes] }).ToList();

        Assert.Equal("too_many_files", PullRequestBuilder.Validate(many, [])!.ErrorCode);
        Assert.Equal("file_too_large", PullRequestBuilder.Validate([big], [])!.ErrorCode);
        Assert.Equal("change_set_too_large", PullRequestBuilder.Validate(total, [])!.ErrorCode);
        Assert.Equal("no_changes", PullRequestBuilder.Validate([], [])!.ErrorCode);
        Assert.Null(PullRequestBuilder.Validate([file], []));
    }

    [Theory]
    [InlineData(".github/workflows/**", ".github/workflows/ci.yml", true)]
    [InlineData(".github/workflows/**", ".github/workflows/sub/x.yml", true)]
    [InlineData(".github/workflows/**", ".github/dependabot.yml", false)]
    [InlineData("**/*.pem", "a/b/key.pem", true)]
    [InlineData("**/*.pem", "key.pem", true)]
    [InlineData("deploy/*", "deploy/a/b", false)]
    public void Blocked_path_globs(string pattern, string path, bool expected) =>
        Assert.Equal(expected, PathGlob.IsMatch(pattern, path));
}
