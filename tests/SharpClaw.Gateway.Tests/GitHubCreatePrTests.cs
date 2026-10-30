using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;
using static SharpClaw.Gateway.Tests.BrokerTestHelpers;

namespace SharpClaw.Gateway.Tests;

/// <summary>
/// <c>github_create_pr</c> (spec section 7.7) end to end: gateway clone record → scripted runner
/// change set → hardline checks → approval over the change set → fork-token branch build → PR.
/// </summary>
public partial class GitHubCreatePrTests
{
    private const string Base = "1111111111111111111111111111111111111111";
    private const string Bot = "sharpclaw-bot";
    private static readonly object PrArgs = new { clonePath = "repos/repo", title = "Fix the bug", body = "Details.", branch = "fix-bug" };

    private sealed record Ctx(GatewayFixture Fx, Turn Turn, FakeGitHub GitHub, FakeRunner Runner) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Fx.DisposeAsync();
            await Runner.DisposeAsync();
        }
    }

    private static object File(string path, string status = "modified", string mode = "100644", string? text = "new\n", int add = 1, int del = 1) =>
        new { path, status, mode, additions = add, deletions = del, content = text is null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) };

    private static async Task<Ctx> SetupAsync(object[]? files = null, Dictionary<string, string?>? extra = null, bool record = true)
    {
        var runner = await FakeRunner.StartAsync();
        files ??= [File("src/a.cs"), File("docs/new.md", "added", add: 3, del: 0), File("old.txt", "deleted", text: null, add: 0, del: 4)];
        runner.Reply = (op, _) => op == "git_changes" ? new { ok = true, path = "repos/repo", files } : new { ok = false };
        var github = new FakeGitHub();
        var config = FakeGitHub.Tokens(runner.Config("assistant", BrokerTools.GitHubCreatePr));
        config["GitHub:ForkOwner"] = Bot;
        config["GitHub:Repos:0"] = "octo/repo";
        foreach (var kv in extra ?? new())
            config[kv.Key] = kv.Value;
        var fx = await GatewayFixture.CreateAsync(extraConfig: config, configure: github.Install());
        var turn = await fx.ClaimedTurnAsync();
        if (record)
        {
            await using var db = fx.NewDb();
            db.SandboxClones.Add(new SandboxClone
            {
                Id = Guid.NewGuid(), AgentId = turn.Agent.Id, Path = "repos/repo", Host = "github.com", Repo = "octo/repo",
                BaseSha = Base, CreatedAt = fx.Time.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync();
        }
        GitHubRoutes(github);
        return new Ctx(fx, turn, github, runner);
    }

    /// <summary>A healthy fork and upstream; the branch doesn't exist yet.</summary>
    private static void GitHubRoutes(FakeGitHub gh)
    {
        gh.On($"GET /repos/{Bot}/repo", new { full_name = $"{Bot}/repo", fork = true, parent = new { full_name = "octo/repo" }, default_branch = "main" });
        gh.On("GET /repos/octo/repo", new { full_name = "octo/repo", fork = false, default_branch = "main" });
        gh.On($"POST /repos/{Bot}/repo/git/refs", new { @ref = "refs/heads/sharpclaw/assistant/fix-bug", @object = new { sha = Base } }, HttpStatusCode.Created);
        gh.On($"GET /repos/{Bot}/repo/git/commits/{Base}", new { sha = Base, tree = new { sha = "basetree" } });
        gh.On($"POST /repos/{Bot}/repo/git/blobs", new { sha = "blobsha" }, HttpStatusCode.Created);
        gh.On($"POST /repos/{Bot}/repo/git/trees", new { sha = "newtree" }, HttpStatusCode.Created);
        gh.On($"POST /repos/{Bot}/repo/git/commits", new { sha = "newcommit" }, HttpStatusCode.Created);
        gh.On($"PATCH /repos/{Bot}/repo/git/refs/heads/sharpclaw/assistant/fix-bug", new { @ref = "refs/heads/sharpclaw/assistant/fix-bug", @object = new { sha = "newcommit" } });
        gh.On("POST /repos/octo/repo/pulls", new
        {
            number = 42, title = "Fix the bug", state = "open", html_url = "https://github.com/octo/repo/pull/42",
            @base = new { @ref = "main" }, head = new { @ref = "sharpclaw/assistant/fix-bug" },
        }, HttpStatusCode.Created);
    }

    /// <summary>Requests an approval like the agent does; returns its id and detail, or the refusal.</summary>
    private static async Task<(Guid Id, string? Detail, HttpResponseMessage Response)> RequestAsync(Turn t, object args)
    {
        var element = Args(args);
        using var c = t.Fx.AgentClient(t.Token);
        var resp = await c.PostAsJsonAsync("/agent/approvals", new CreateApprovalRequest(
            t.Corr, t.Thread, "call_1", BrokerTools.GitHubCreatePr, "pull request", RiskLevels.High, "opens a PR",
            ToolArgsHash.Compute(element), element), SharpClawJson.Options);
        if (!resp.IsSuccessStatusCode)
            return (Guid.Empty, null, resp);
        var id = (await resp.Content.ReadFromJsonAsync<ApprovalResponse>(SharpClawJson.Options))!.Approval.Id;
        using var admin = t.Fx.Admin();
        var detail = (await admin.GetFromJsonAsync<ApprovalDetailResponse>($"/api/approvals/{id}", SharpClawJson.Options))!.Approval.Detail;
        return (id, detail, resp);
    }

    private static async Task ApproveAsync(Turn t, Guid id)
    {
        using var admin = t.Fx.Admin();
        (await admin.PostAsJsonAsync($"/api/approvals/{id}", new DecideApprovalRequest(ApprovalStatuses.Approved), SharpClawJson.Options)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Opens_a_pr_from_the_fork_branch_after_an_approval_listing_the_files()
    {
        await using var ctx = await SetupAsync();
        var (id, detail, _) = await RequestAsync(ctx.Turn, PrArgs);
        Assert.Equal("""
            PR to octo/repo from sharpclaw-bot:sharpclaw/assistant/fix-bug
            title: Fix the bug
            3 files, +4 −5
              A docs/new.md (+3 −0)
              D old.txt (+0 −4)
              M src/a.cs (+1 −1)
            """.Replace("\r", ""), detail);
        Assert.DoesNotContain("new\n", detail!);
        await ApproveAsync(ctx.Turn, id);

        var resp = await ctx.Turn.InvokeAsync(BrokerTools.GitHubCreatePr, PrArgs, id);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("ok").GetBoolean(), body.GetRawText());
        Assert.Equal((42, "sharpclaw-bot:sharpclaw/assistant/fix-bug", 3), (body.GetProperty("number").GetInt32(),
            body.GetProperty("branch").GetString(), body.GetProperty("files").GetInt32()));

        var requests = ctx.GitHub.Requests.ToList();
        var pull = requests.Single(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/repos/octo/repo/pulls");
        var pr = JsonDocument.Parse(pull.Body!).RootElement;
        Assert.Equal(("sharpclaw-bot:sharpclaw/assistant/fix-bug", "main"), (pr.GetProperty("head").GetString(), pr.GetProperty("base").GetString()));
        Assert.Contains("Opened by the SharpClaw agent `assistant`", pr.GetProperty("body").GetString());
        // Each token only where it belongs: the fork token on the bot's fork, the owner token upstream.
        Assert.All(requests.Where(r => r.Uri.AbsolutePath.StartsWith($"/repos/{Bot}/")), r => Assert.Equal(FakeGitHub.ForkToken, r.Token));
        Assert.All(requests.Where(r => r.Uri.AbsolutePath.StartsWith("/repos/octo/")), r => Assert.Equal(FakeGitHub.OwnerToken, r.Token));
        // The ref was created at the base, then fast-forwarded, never forced.
        var patch = requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.Contains("\"force\":false", patch.Body);
        var tree = JsonDocument.Parse(requests.Single(r => r.Uri.AbsolutePath.EndsWith("/git/trees")).Body!).RootElement;
        Assert.Equal("basetree", tree.GetProperty("base_tree").GetString());
        Assert.Contains(tree.GetProperty("tree").EnumerateArray(), e => e.GetProperty("path").GetString() == "old.txt" && e.GetProperty("sha").ValueKind == JsonValueKind.Null);

        await using var db = ctx.Fx.NewDb();
        Assert.Equal("octo/repo#42 (3 files, +4 −5)", (await db.BrokerCalls.SingleAsync()).Target);
    }

    [Fact]
    public async Task A_change_set_that_differs_from_the_approved_one_is_refused()
    {
        await using var ctx = await SetupAsync();
        var (id, _, _) = await RequestAsync(ctx.Turn, PrArgs);
        await ApproveAsync(ctx.Turn, id);
        ctx.Runner.Reply = (_, _) => new { ok = true, path = "repos/repo", files = new[] { File("src/a.cs", text: "sneaky edit after approval\n") } };

        var resp = await ctx.Turn.InvokeAsync(BrokerTools.GitHubCreatePr, PrArgs, id);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(ErrorCodes.ApprovalRequired, await ClientApiHelpers.ErrorCodeAsync(resp));
        Assert.DoesNotContain(ctx.GitHub.Requests, r => r.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task The_origin_comes_from_the_gateways_record_not_the_sandbox()
    {
        await using var ctx = await SetupAsync();
        ctx.Runner.Reply = (_, _) => new { ok = true, path = "repos/repo", origin = "attacker/repo", repo = "attacker/repo", files = new[] { File("a.txt") } };
        var (id, detail, _) = await RequestAsync(ctx.Turn, PrArgs);
        await ApproveAsync(ctx.Turn, id);

        await ctx.Turn.InvokeAsync(BrokerTools.GitHubCreatePr, PrArgs, id);

        Assert.StartsWith("PR to octo/repo", detail);
        Assert.DoesNotContain(ctx.GitHub.Requests, r => r.Uri.AbsolutePath.Contains("attacker"));
        Assert.Contains(ctx.Runner.Calls, c => c.Op == "git_changes" && c.Args.GetProperty("base").GetString() == Base);
    }
}
