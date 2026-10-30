using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using static SharpClaw.Gateway.Tests.BrokerTestHelpers;

namespace SharpClaw.Gateway.Tests;

/// <summary>
/// <c>git_clone</c> in the gateway (spec section 7.4): the host allow-list is enforced before the
/// sandbox is asked, and the clone's origin and base commit are recorded from the runner's answer.
/// </summary>
public class GitCloneTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static object Cloned(JsonElement args, string host = "github.com", string repo = "owner/repo", string sha = Sha) =>
        new { ok = true, path = args.GetProperty("path").GetString(), host, repo, commit = sha, bytes = 10 };

    [Fact]
    public async Task A_successful_clone_is_recorded_by_the_gateway()
    {
        await using var runner = await FakeRunner.StartAsync();
        runner.Reply = (_, args) => Cloned(args);
        await using var fx = await GatewayAsync(runner.Config("assistant", BrokerTools.GitClone));
        var t = await fx.ClaimedTurnAsync();

        var resp = await t.InvokeAsync(BrokerTools.GitClone, new { url = "https://github.com/owner/repo.git", path = "repos/repo" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await using var db = fx.NewDb();
        var clone = await db.SandboxClones.SingleAsync();
        Assert.Equal((t.Agent.Id, "repos/repo", "github.com", "owner/repo", Sha),
            (clone.AgentId, clone.Path, clone.Host, clone.Repo, clone.BaseSha));
        Assert.Equal("github.com/owner/repo", (await db.BrokerCalls.SingleAsync()).Target);
    }

    [Fact]
    public async Task Cloning_again_to_the_same_path_replaces_the_record()
    {
        await using var runner = await FakeRunner.StartAsync();
        var sha = Sha;
        runner.Reply = (_, args) => Cloned(args, sha: sha);
        await using var fx = await GatewayAsync(runner.Config("assistant", BrokerTools.GitClone));
        var t = await fx.ClaimedTurnAsync();

        await t.InvokeAsync(BrokerTools.GitClone, new { url = "https://github.com/owner/repo", path = "r" });
        sha = new string('f', 40);
        await t.InvokeAsync(BrokerTools.GitClone, new { url = "https://github.com/owner/repo", path = "r" });

        await using var db = fx.NewDb();
        Assert.Equal(sha, (await db.SandboxClones.SingleAsync()).BaseSha);
    }

    [Theory]
    [InlineData("https://evil.example/owner/repo")]
    [InlineData("https://bitbucket.org/owner/repo")]
    public async Task Hosts_off_the_allow_list_are_refused_before_the_sandbox_is_asked(string url)
    {
        await using var runner = await FakeRunner.StartAsync();
        await using var fx = await GatewayAsync(runner.Config("assistant", BrokerTools.GitClone));
        var t = await fx.ClaimedTurnAsync();

        var resp = await t.InvokeAsync(BrokerTools.GitClone, new { url, path = "r" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("host_not_allowed", await ClientApiHelpers.ErrorCodeAsync(resp));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task The_operator_sets_the_allowed_hosts()
    {
        await using var runner = await FakeRunner.StartAsync();
        runner.Reply = (_, args) => Cloned(args, host: "git.example.org", repo: "team/tool");
        var config = runner.Config("assistant", BrokerTools.GitClone);
        config["ToolBroker:CloneAllowHosts:0"] = "git.example.org";
        await using var fx = await GatewayAsync(config);
        var t = await fx.ClaimedTurnAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await t.InvokeAsync(BrokerTools.GitClone, new { url = "https://github.com/a/b", path = "a" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t.InvokeAsync(BrokerTools.GitClone, new { url = "https://git.example.org/team/tool", path = "b" })).StatusCode);
    }

    [Fact]
    public async Task A_runner_answer_for_another_repository_is_not_recorded()
    {
        await using var runner = await FakeRunner.StartAsync();
        runner.Reply = (_, args) => Cloned(args, repo: "attacker/other");
        await using var fx = await GatewayAsync(runner.Config("assistant", BrokerTools.GitClone));
        var t = await fx.ClaimedTurnAsync();

        var resp = await t.InvokeAsync(BrokerTools.GitClone, new { url = "https://github.com/owner/repo", path = "r" });

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        await using var db = fx.NewDb();
        Assert.Empty(await db.SandboxClones.ToListAsync());
    }

    [Fact]
    public async Task A_failed_clone_is_returned_and_not_recorded()
    {
        await using var runner = await FakeRunner.StartAsync();
        runner.Reply = (_, _) => new { ok = false, error = "clone_failed", message = "nope" };
        await using var fx = await GatewayAsync(runner.Config("assistant", BrokerTools.GitClone));
        var t = await fx.ClaimedTurnAsync();

        var resp = await t.InvokeAsync(BrokerTools.GitClone, new { url = "https://github.com/owner/repo", path = "r" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await using var db = fx.NewDb();
        Assert.Empty(await db.SandboxClones.ToListAsync());
    }
}
