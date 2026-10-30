using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SharpClaw.Contracts;
using static SharpClaw.Gateway.Tests.BrokerTestHelpers;

namespace SharpClaw.Gateway.Tests;

/// <summary><c>github_write</c> (spec section 7.6, Q2, Q4): always approved, the human sees what will be posted.</summary>
public class GitHubWriteTests
{
    private static async Task<(GatewayFixture Fx, Turn Turn, FakeGitHub GitHub)> SetupAsync(Dictionary<string, string?>? extra = null)
    {
        var github = new FakeGitHub();
        var config = FakeGitHub.Tokens(Config("assistant", BrokerTools.GitHubWrite));
        foreach (var kv in extra ?? new())
            config[kv.Key] = kv.Value;
        var fx = await GatewayFixture.CreateAsync(extraConfig: config, configure: github.Install());
        return (fx, await fx.ClaimedTurnAsync(), github);
    }

    /// <summary>Requests (with arguments, like the agent) and approves an approval; returns it and its detail.</summary>
    private static async Task<(Guid Id, string? Detail)> ApproveAsync(Turn t, object args)
    {
        var element = Args(args);
        using var c = t.Fx.AgentClient(t.Token);
        var created = await c.PostAsJsonAsync("/agent/approvals", new CreateApprovalRequest(
            t.Corr, t.Thread, "call_1", BrokerTools.GitHubWrite, "comment", RiskLevels.High, "writes",
            ToolArgsHash.Compute(element), element), SharpClawJson.Options);
        var id = (await created.Content.ReadFromJsonAsync<ApprovalResponse>(SharpClawJson.Options))!.Approval.Id;
        using var admin = t.Fx.Admin();
        (await admin.PostAsJsonAsync($"/api/approvals/{id}", new DecideApprovalRequest(ApprovalStatuses.Approved), SharpClawJson.Options)).EnsureSuccessStatusCode();
        var detail = (await admin.GetFromJsonAsync<ApprovalDetailResponse>($"/api/approvals/{id}", SharpClawJson.Options))!.Approval.Detail;
        return (id, detail);
    }

    [Fact]
    public async Task A_comment_is_posted_only_with_an_approval_that_shows_its_text()
    {
        var (fx, t, gh) = await SetupAsync();
        await using var _ = fx;
        gh.On("POST /repos/octo/repo/issues/12/comments", new { id = 99, html_url = "https://github.com/octo/repo/issues/12#c99", created_at = "2026-10-30T00:00:00Z" }, HttpStatusCode.Created);
        var args = new { operation = "comment", owner = "octo", repo = "repo", number = 12, body = "LGTM — thanks!" };

        var refused = await t.InvokeAsync(BrokerTools.GitHubWrite, args);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Empty(gh.Requests);

        var (id, detail) = await ApproveAsync(t, args);
        Assert.Equal("comment octo/repo#12\n---\nLGTM — thanks!", detail);

        var resp = await t.InvokeAsync(BrokerTools.GitHubWrite, args, id);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("commented").GetBoolean());
        var request = Assert.Single(gh.Requests);
        Assert.Equal((FakeGitHub.OwnerToken, """{"body":"LGTM \u2014 thanks!"}"""), (request.Token, request.Body));
    }

    [Fact]
    public async Task Create_issue_add_and_remove_labels()
    {
        var (fx, t, gh) = await SetupAsync(new() { ["ToolBroker:AlwaysApprove:0"] = "" });
        await using var _ = fx;
        gh.On("POST /repos/o/r/issues", new { number = 7, title = "T", state = "open", created_at = "2026-10-30T00:00:00Z", html_url = "https://github.com/o/r/issues/7" }, HttpStatusCode.Created);
        gh.On("POST /repos/o/r/issues/7/labels", new[] { new { name = "bug" }, new { name = "p1" } });
        gh.On("DELETE /repos/o/r/issues/7/labels/needs%20triage", Array.Empty<object>());

        var created = await (await t.InvokeAsync(BrokerTools.GitHubWrite, new { operation = "create_issue", owner = "o", repo = "r", title = "T", body = "B", labels = new[] { "bug" } })).Content.ReadFromJsonAsync<JsonElement>();
        var added = await (await t.InvokeAsync(BrokerTools.GitHubWrite, new { operation = "add_labels", owner = "o", repo = "r", number = 7, labels = new[] { "bug", "p1" } })).Content.ReadFromJsonAsync<JsonElement>();
        var removed = await (await t.InvokeAsync(BrokerTools.GitHubWrite, new { operation = "remove_labels", owner = "o", repo = "r", number = 7, labels = new[] { "needs triage", "absent" } })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(7, created.GetProperty("number").GetInt32());
        Assert.Equal(2, added.GetProperty("labels").GetArrayLength());
        Assert.Equal(new[] { "needs triage" }, removed.GetProperty("removed").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains(gh.Requests, r => r.Method == HttpMethod.Delete && r.Uri.AbsolutePath.EndsWith("/labels/needs%20triage"));
    }

    [Theory]
    [InlineData("""{"operation":"merge","owner":"o","repo":"r","number":1}""")]
    [InlineData("""{"operation":"comment","owner":"o","repo":"r","number":1}""")]
    [InlineData("""{"operation":"create_issue","owner":"o","repo":"r","body":"no title"}""")]
    [InlineData("""{"operation":"add_labels","owner":"o","repo":"r","number":1,"labels":[]}""")]
    [InlineData("""{"operation":"add_labels","owner":"o","repo":"r","number":1,"labels":["a","b","c","d","e","f","g","h","i","j","k"]}""")]
    public async Task Bad_writes_never_reach_github(string args)
    {
        var (fx, t, gh) = await SetupAsync(new() { ["ToolBroker:AlwaysApprove:0"] = "" });
        await using var _ = fx;

        var body = await (await t.InvokeAsync(BrokerTools.GitHubWrite, JsonDocument.Parse(args).RootElement)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(ErrorCodes.InvalidArguments, body.GetProperty("error").GetString());
        Assert.Empty(gh.Requests);
    }

    [Fact]
    public async Task Writes_respect_the_repo_allow_list()
    {
        var (fx, t, gh) = await SetupAsync(new() { ["ToolBroker:AlwaysApprove:0"] = "", ["GitHub:Repos:0"] = "o/allowed" });
        await using var _ = fx;

        var resp = await t.InvokeAsync(BrokerTools.GitHubWrite, new { operation = "comment", owner = "o", repo = "other", number = 1, body = "x" });

        Assert.Equal("repo_not_allowed", await ClientApiHelpers.ErrorCodeAsync(resp));
        Assert.Empty(gh.Requests);
    }
}
