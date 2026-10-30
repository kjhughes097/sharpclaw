using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts;
using SharpClaw.Gateway.GitHub;
using static SharpClaw.Gateway.Tests.BrokerTestHelpers;

namespace SharpClaw.Gateway.Tests;

/// <summary><c>github_read</c> (spec section 7.6) against a fake api.github.com.</summary>
public class GitHubReadTests
{
    private static async Task<(GatewayFixture Fx, Turn Turn, FakeGitHub GitHub)> SetupAsync(
        Dictionary<string, string?>? extra = null, bool tokens = true)
    {
        var github = new FakeGitHub();
        var config = Config("assistant", BrokerTools.GitHubRead);
        if (tokens)
            FakeGitHub.Tokens(config);
        foreach (var kv in extra ?? new())
            config[kv.Key] = kv.Value;
        var fx = await GatewayFixture.CreateAsync(extraConfig: config, configure: github.Install());
        return (fx, await fx.ClaimedTurnAsync(), github);
    }

    private static async Task<JsonElement> Body(HttpResponseMessage resp) => await resp.Content.ReadFromJsonAsync<JsonElement>();

    private static readonly object Issue = new
    {
        number = 12, title = "Bug", state = "open", user = new { login = "alice" }, body = "Steps…",
        labels = new[] { new { name = "bug" } }, created_at = "2026-10-01T00:00:00Z", html_url = "https://github.com/o/r/issues/12",
        node_id = "SECRET_EXTRA_FIELD", reactions = new { total_count = 3 },
    };

    [Fact]
    public async Task Get_issue_is_reduced_to_the_fields_the_model_needs()
    {
        var (fx, t, gh) = await SetupAsync();
        await using var _ = fx;
        gh.On("GET /repos/octo/repo/issues/12", Issue);

        var body = await Body(await t.InvokeAsync(BrokerTools.GitHubRead, new { operation = "get_issue", owner = "octo", repo = "repo", number = 12 }));

        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(("Bug", "alice", "bug"), (body.GetProperty("title").GetString(), body.GetProperty("author").GetString(),
            body.GetProperty("labels")[0].GetString()));
        Assert.DoesNotContain("SECRET_EXTRA_FIELD", body.GetRawText());
        var request = Assert.Single(gh.Requests);
        Assert.Equal(("api.github.com", FakeGitHub.OwnerToken), (request.Uri.Host, request.Token));
    }

    [Fact]
    public async Task Lists_mark_pull_requests_and_pages()
    {
        var (fx, t, gh) = await SetupAsync();
        await using var _ = fx;
        gh.On("GET /repos/octo/repo/issues", new object[] { Issue, new { number = 13, title = "PR", state = "open", created_at = "2026-10-01T00:00:00Z", pull_request = new { url = "x" } } });

        var body = await Body(await t.InvokeAsync(BrokerTools.GitHubRead, new { operation = "list_issues", owner = "octo", repo = "repo", state = "all", page = 2 }));

        Assert.Equal(new[] { false, true }, body.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("isPullRequest").GetBoolean()));
        Assert.Contains("state=all", gh.Requests.Single().Uri.Query);
        Assert.Contains("page=2", gh.Requests.Single().Uri.Query);
    }

    [Fact]
    public async Task Pull_diffs_use_the_diff_media_type_and_are_capped()
    {
        var (fx, t, gh) = await SetupAsync();
        await using var _ = fx;
        gh.Routes["GET /repos/octo/repo/pulls/5"] = _ => FakeGitHub.Text("diff --git a/x b/x\n" + new string('+', 70_000));

        var body = await Body(await t.InvokeAsync(BrokerTools.GitHubRead, new { operation = "get_pull_diff", owner = "octo", repo = "repo", number = 5 }));

        Assert.True(body.GetProperty("truncated").GetBoolean());
        Assert.True(Encoding.UTF8.GetByteCount(body.GetRawText()) <= 64 * 1024);
        Assert.Equal("application/vnd.github.diff", gh.Requests.Single().Accept);
    }

    [Fact]
    public async Task Files_are_decoded_and_binary_or_directories_described()
    {
        var (fx, t, gh) = await SetupAsync();
        await using var _ = fx;
        gh.On("GET /repos/octo/repo/contents/src/a%20b.txt", new { type = "file", name = "a b.txt", path = "src/a b.txt", size = 5, encoding = "base64", content = Convert.ToBase64String("hello"u8.ToArray()) });
        gh.On("GET /repos/octo/repo/contents/bin.dat", new { type = "file", name = "bin.dat", path = "bin.dat", size = 3, encoding = "base64", content = Convert.ToBase64String(new byte[] { 0, 1, 2 }) });
        gh.On("GET /repos/octo/repo/contents/src", new[] { new { type = "file", name = "a b.txt", path = "src/a b.txt", size = 5 } });

        var text = await Body(await t.InvokeAsync(BrokerTools.GitHubRead, new { operation = "get_file", owner = "octo", repo = "repo", path = "src/a b.txt", @ref = "main" }));
        var binary = await Body(await t.InvokeAsync(BrokerTools.GitHubRead, new { operation = "get_file", owner = "octo", repo = "repo", path = "bin.dat" }));
        var dir = await Body(await t.InvokeAsync(BrokerTools.GitHubRead, new { operation = "get_file", owner = "octo", repo = "repo", path = "src" }));

        Assert.Equal("hello", text.GetProperty("content").GetString());
        Assert.Contains("ref=main", gh.Requests.First().Uri.Query);
        Assert.True(binary.GetProperty("binary").GetBoolean());
        Assert.True(dir.GetProperty("directory").GetBoolean());
    }

    [Theory]
    [InlineData("""{"operation":"delete_repo","owner":"o","repo":"r"}""")]
    [InlineData("""{"operation":"get_issue","owner":"o","repo":"r"}""")]
    [InlineData("""{"operation":"get_issue","owner":"../x","repo":"r","number":1}""")]
    [InlineData("""{"operation":"get_file","owner":"o","repo":"r","path":"../../etc"}""")]
    [InlineData("""{"operation":"get_file","owner":"o","repo":"r","path":"a","ref":"x..y"}""")]
    [InlineData("""{"operation":"list_issues","owner":"o","repo":"r","state":"merged"}""")]
    public async Task Only_valid_enum_operations_reach_github(string args)
    {
        var (fx, t, gh) = await SetupAsync();
        await using var _ = fx;

        var body = await Body(await t.InvokeAsync(BrokerTools.GitHubRead, JsonDocument.Parse(args).RootElement));

        Assert.Equal(ErrorCodes.InvalidArguments, body.GetProperty("error").GetString());
        Assert.Empty(gh.Requests);
    }

    [Fact]
    public async Task The_repo_allow_list_refuses_before_any_request()
    {
        var (fx, t, gh) = await SetupAsync(new() { ["GitHub:Repos:0"] = "octo/allowed" });
        await using var _ = fx;
        gh.On("GET /repos/octo/allowed/issues/1", Issue);

        var refused = await t.InvokeAsync(BrokerTools.GitHubRead, new { operation = "get_issue", owner = "octo", repo = "other", number = 1 });
        var allowed = await t.InvokeAsync(BrokerTools.GitHubRead, new { operation = "get_issue", owner = "OCTO", repo = "Allowed", number = 1 });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("repo_not_allowed", await ClientApiHelpers.ErrorCodeAsync(refused));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Single(gh.Requests);
    }

    [Fact]
    public async Task Without_a_token_github_is_unavailable()
    {
        var (fx, t, gh) = await SetupAsync(tokens: false);
        await using var _ = fx;

        var resp = await t.InvokeAsync(BrokerTools.GitHubRead, new { operation = "get_issue", owner = "o", repo = "r", number = 1 });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Empty(gh.Requests);
    }

    [Fact]
    public async Task Redirects_are_never_followed_and_errors_are_tool_errors()
    {
        var (fx, t, gh) = await SetupAsync();
        await using var _ = fx;
        gh.Routes["GET /repos/o/r/issues/1"] = _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example/steal") } };

        var redirect = await Body(await t.InvokeAsync(BrokerTools.GitHubRead, new { operation = "get_issue", owner = "o", repo = "r", number = 1 }));
        var missing = await Body(await t.InvokeAsync(BrokerTools.GitHubRead, new { operation = "get_issue", owner = "o", repo = "r", number = 2 }));

        Assert.Equal("github_error", redirect.GetProperty("error").GetString());
        Assert.Equal("not_found", missing.GetProperty("error").GetString());
        Assert.All(gh.Requests, r => Assert.Equal("api.github.com", r.Uri.Host));
        Assert.Equal(2, gh.Requests.Count);
    }

    [Fact]
    public async Task Long_lists_are_cut_to_64_KB()
    {
        var (fx, t, gh) = await SetupAsync();
        await using var _ = fx;
        gh.On("GET /repos/o/r/issues/1/comments", Enumerable.Range(0, 30)
            .Select(i => new { user = new { login = "u" }, body = new string('x', 7000), created_at = "2026-10-01T00:00:00Z" }).ToArray());

        var body = await Body(await t.InvokeAsync(BrokerTools.GitHubRead, new { operation = "list_comments", owner = "o", repo = "r", number = 1 }));

        Assert.True(body.GetProperty("truncated").GetBoolean());
        Assert.True(Encoding.UTF8.GetByteCount(body.GetRawText()) <= 64 * 1024);
        Assert.InRange(body.GetProperty("comments").GetArrayLength(), 1, 29);
    }

    [Fact]
    public async Task The_real_clients_are_fixed_to_api_github_com_and_never_follow_redirects()
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: FakeGitHub.Tokens(new()));
        var factory = fx.App.Services.GetRequiredService<IHttpClientFactory>();
        var handlers = fx.App.Services.GetRequiredService<IHttpMessageHandlerFactory>();

        foreach (var (name, token) in new[] { (GitHubClient.OwnerClient, FakeGitHub.OwnerToken), (GitHubClient.ForkClient, FakeGitHub.ForkToken) })
        {
            using var client = factory.CreateClient(name);
            Assert.Equal(new Uri("https://api.github.com/"), client.BaseAddress);
            Assert.Equal(token, client.DefaultRequestHeaders.Authorization!.Parameter);
            HttpMessageHandler handler = handlers.CreateHandler(name);
            while (handler is DelegatingHandler d)
                handler = d.InnerHandler!;
            var sockets = Assert.IsType<SocketsHttpHandler>(handler);
            Assert.False(sockets.AllowAutoRedirect);
        }

        var github = fx.App.Services.GetRequiredService<GitHubClient>();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            github.SendAsync<bool>(GitHubClient.OwnerClient, HttpMethod.Get, "//evil.example/x", null, default));
    }
}
