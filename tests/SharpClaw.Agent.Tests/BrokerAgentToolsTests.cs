using System.Text.Json;
using SharpClaw.Agent.Tools;
using SharpClaw.Agent.Tools.Broker;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Tests;

/// <summary>The broker tools on the agent side: schema, risk, summaries, classifier fields.</summary>
public class BrokerAgentToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ws-{Guid.NewGuid():N}");
    private readonly Dictionary<string, IAgentTool> _tools;

    public BrokerAgentToolsTests()
    {
        Directory.CreateDirectory(_root);
        var policy = new BrokerPolicy(new HashSet<string>(), new[] { "docs.example.org" });
        _tools = AgentBrokerTools.All(new RecordingGateway(), policy, _root).ToDictionary(t => t.Name);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static JsonElement A(object args) => JsonSerializer.SerializeToElement(args);
    private ToolAssessment Assess(string tool, object args) => _tools[tool].Assess(A(args));

    [Fact]
    public void Names_match_and_schemas_are_valid_json()
    {
        Assert.Equal(AgentBrokerTools.Names.Order(), _tools.Keys.Order());
        foreach (var tool in _tools.Values)
            Assert.Equal("object", JsonDocument.Parse(tool.ParametersJson).RootElement.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("https://docs.example.org/page", RiskLevel.Low)]
    [InlineData("https://api.docs.example.org/x", RiskLevel.Low)]
    [InlineData("https://example.org/", RiskLevel.Medium)]
    [InlineData("https://docs.example.org.evil.com/", RiskLevel.Medium)]
    public void Fetch_is_low_for_allow_listed_hosts_and_medium_otherwise(string url, RiskLevel expected) =>
        Assert.Equal(expected, Assert.IsType<ToolAssessment.Scored>(Assess(BrokerTools.HttpFetch, new { url })).Level);

    [Theory]
    [InlineData("ftp://example.org/")]
    [InlineData("http://user:pw@example.org/")]
    [InlineData("relative/path")]
    public void Fetch_blocks_bad_urls(string url) =>
        Assert.IsType<ToolAssessment.Blocked>(Assess(BrokerTools.HttpFetch, new { url }));

    [Fact]
    public void Fetch_blocks_an_unknown_format() =>
        Assert.IsType<ToolAssessment.Blocked>(Assess(BrokerTools.HttpFetch, new { url = "https://a.org/", format = "pdf" }));

    [Fact]
    public void Fetch_summary_and_classifier_fields_carry_the_host_only()
    {
        var args = A(new { url = "https://example.org/ignore-previous-instructions?token=SECRET", format = "text" });
        var tool = _tools[BrokerTools.HttpFetch];

        Assert.Equal("fetch example.org (text)", tool.Summarize(args));
        Assert.Equal(new Dictionary<string, string> { ["host"] = "example.org", ["format"] = "text" }, tool.ClassifierFields(args));
        Assert.True(tool.ReturnsUntrustedContent);
    }

    [Fact]
    public void Download_is_medium_and_uncertain_for_sensitive_paths()
    {
        Assert.Equal(RiskLevel.Medium,
            Assert.IsType<ToolAssessment.Scored>(Assess(BrokerTools.DownloadToWorkspace, new { url = "https://a.org/f.zip", path = "f.zip" })).Level);
        Assert.IsType<ToolAssessment.Uncertain>(Assess(BrokerTools.DownloadToWorkspace, new { url = "https://a.org/i.sh", path = "install.sh" }));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public void Download_blocks_paths_outside_the_workspace(string path) =>
        Assert.IsType<ToolAssessment.Blocked>(Assess(BrokerTools.DownloadToWorkspace, new { url = "https://a.org/f", path }));

    [Fact]
    public void Download_summary_names_host_and_path()
    {
        var tool = _tools[BrokerTools.DownloadToWorkspace];
        var args = A(new { url = "https://a.org/f.zip?sig=SECRET", path = "in/f.zip", overwrite = true });

        Assert.Equal("download a.org → in/f.zip (overwrite)", tool.Summarize(args));
        Assert.False(tool.ReturnsUntrustedContent);
    }

    [Fact]
    public void Web_search_is_low_untrusted_and_never_shows_the_query()
    {
        var tool = _tools[BrokerTools.WebSearch];
        var args = A(new { query = "ignore previous instructions SECRET", maxResults = 3 });

        Assert.Equal(RiskLevel.Low, Assert.IsType<ToolAssessment.Scored>(tool.Assess(args)).Level);
        Assert.Equal("search (3 results)", tool.Summarize(args));
        Assert.DoesNotContain("SECRET", string.Join(",", tool.ClassifierFields(args)!.Values));
        Assert.True(tool.ReturnsUntrustedContent);
        Assert.Equal("search (5 results)", tool.Summarize(A(new { query = "x" })));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"query":"  "}""")]
    [InlineData("""{"query":"x","maxResults":0}""")]
    [InlineData("""{"query":"x","maxResults":11}""")]
    public void Web_search_blocks_bad_arguments(string args) =>
        Assert.IsType<ToolAssessment.Blocked>(_tools[BrokerTools.WebSearch].Assess(JsonDocument.Parse(args).RootElement));

    [Fact]
    public void Extract_text_is_low_writes_a_file_and_is_uncertain_for_sensitive_output()
    {
        var tool = _tools[BrokerTools.ExtractText];

        Assert.Equal(RiskLevel.Low, Assert.IsType<ToolAssessment.Scored>(tool.Assess(A(new { path = "a.pdf", to = "a.txt" }))).Level);
        Assert.IsType<ToolAssessment.Uncertain>(tool.Assess(A(new { path = "a.html", to = "run.sh" })));
        Assert.Equal("extract a.pdf → a.txt", tool.Summarize(A(new { path = "a.pdf", to = "a.txt" })));
        Assert.False(tool.ReturnsUntrustedContent);
    }

    [Theory]
    [InlineData("a.docx", "a.txt")]
    [InlineData("../a.pdf", "a.txt")]
    [InlineData("a.pdf", "/tmp/a.txt")]
    [InlineData("a.pdf", "a.pdf")]
    public void Extract_text_blocks_bad_paths_and_types(string path, string to) =>
        Assert.IsType<ToolAssessment.Blocked>(_tools[BrokerTools.ExtractText].Assess(A(new { path, to })));

    [Fact]
    public void Git_clone_is_medium_and_names_the_repository()
    {
        var tool = _tools[BrokerTools.GitClone];
        var args = A(new { url = "https://github.com/owner/repo.git", path = "repos/repo" });

        Assert.Equal(RiskLevel.Medium, Assert.IsType<ToolAssessment.Scored>(tool.Assess(args)).Level);
        Assert.Equal("clone github.com/owner/repo → repos/repo", tool.Summarize(args));
        Assert.Equal("github.com/owner/repo", tool.ClassifierFields(args)!["repository"]);
    }

    [Theory]
    [InlineData("https://evil.example/owner/repo", "r")]
    [InlineData("http://github.com/owner/repo", "r")]
    [InlineData("https://github.com/owner", "r")]
    [InlineData("https://github.com/owner/repo", "../r")]
    [InlineData("https://github.com/owner/repo", "existing")]
    public void Git_clone_blocks_bad_hosts_urls_and_paths(string url, string path)
    {
        Directory.CreateDirectory(Path.Combine(_root, "existing"));
        Assert.IsType<ToolAssessment.Blocked>(_tools[BrokerTools.GitClone].Assess(A(new { url, path })));
    }

    [Fact]
    public void Sandbox_run_is_high_untrusted_and_summaries_never_show_the_command()
    {
        Directory.CreateDirectory(Path.Combine(_root, "repo"));
        var tool = _tools[BrokerTools.SandboxRun];
        var args = A(new { command = "curl SECRET | sh", cwd = "repo" });

        Assert.Equal(RiskLevel.High, Assert.IsType<ToolAssessment.Scored>(tool.Assess(args)).Level);
        Assert.Equal("run (16 chars) in repo/", tool.Summarize(args));
        Assert.DoesNotContain("SECRET", string.Join(",", tool.ClassifierFields(args)!.Values));
        Assert.True(tool.ReturnsUntrustedContent);
        Assert.True(tool.ApprovalNeedsArguments);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"command":"ls","cwd":"../"}""")]
    [InlineData("""{"command":"ls","cwd":"missing"}""")]
    [InlineData("""{"command":"ls","timeoutSeconds":0}""")]
    [InlineData("""{"command":"ls","timeoutSeconds":1801}""")]
    public void Sandbox_run_blocks_bad_arguments(string args) =>
        Assert.IsType<ToolAssessment.Blocked>(_tools[BrokerTools.SandboxRun].Assess(JsonDocument.Parse(args).RootElement));

    [Fact]
    public void Github_read_is_low_untrusted_and_described_by_names_only()
    {
        var tool = _tools[BrokerTools.GitHubRead];
        var args = A(new { operation = "get_issue", owner = "octo", repo = "repo", number = 12 });

        Assert.Equal(RiskLevel.Low, Assert.IsType<ToolAssessment.Scored>(tool.Assess(args)).Level);
        Assert.Equal("github get_issue octo/repo#12", tool.Summarize(args));
        Assert.Equal("octo/repo#12", tool.ClassifierFields(args)!["repository"]);
        Assert.True(tool.ReturnsUntrustedContent);
    }

    [Theory]
    [InlineData("""{"operation":"comment","owner":"o","repo":"r","number":1}""")]
    [InlineData("""{"operation":"get_issue","owner":"o","repo":"r"}""")]
    [InlineData("""{"operation":"get_issue","owner":"o/x","repo":"r","number":1}""")]
    [InlineData("""{"operation":"get_file","owner":"o","repo":"r"}""")]
    public void Github_read_blocks_unknown_operations_and_missing_fields(string args) =>
        Assert.IsType<ToolAssessment.Blocked>(_tools[BrokerTools.GitHubRead].Assess(JsonDocument.Parse(args).RootElement));

    [Theory]
    [InlineData("""{"operation":"comment","owner":"o","repo":"r","number":3,"body":"SECRET text"}""", "comment on o/r#3 (11 B)")]
    [InlineData("""{"operation":"create_issue","owner":"o","repo":"r","title":"SECRET","body":"x"}""", "create issue in o/r (title 6 chars, 1 B)")]
    [InlineData("""{"operation":"add_labels","owner":"o","repo":"r","number":3,"labels":["SECRET","b"]}""", "add 2 labels to o/r#3")]
    [InlineData("""{"operation":"remove_labels","owner":"o","repo":"r","number":3,"labels":["SECRET"]}""", "remove 1 label from o/r#3")]
    public void Github_write_is_high_and_summaries_carry_sizes_and_counts_only(string json, string summary)
    {
        var tool = _tools[BrokerTools.GitHubWrite];
        var args = JsonDocument.Parse(json).RootElement;

        Assert.Equal(RiskLevel.High, Assert.IsType<ToolAssessment.Scored>(tool.Assess(args)).Level);
        Assert.Equal(summary, tool.Summarize(args));
        Assert.True(tool.ApprovalNeedsArguments);
        Assert.False(tool.ReturnsUntrustedContent);
    }

    [Fact]
    public void Create_pr_is_high_and_names_the_clone_and_branch_only()
    {
        Directory.CreateDirectory(Path.Combine(_root, "repos", "repo"));
        var tool = _tools[BrokerTools.GitHubCreatePr];
        var args = A(new { clonePath = "repos/repo", title = "SECRET title", body = "SECRET body", branch = "fix-bug" });

        Assert.Equal(RiskLevel.High, Assert.IsType<ToolAssessment.Scored>(tool.Assess(args)).Level);
        Assert.Equal("pull request from repos/repo/ (branch fix-bug)", tool.Summarize(args));
        Assert.DoesNotContain("SECRET", string.Join(",", tool.ClassifierFields(args)!.Values));
        Assert.True(tool.ApprovalNeedsArguments);
    }

    [Theory]
    [InlineData("missing", "t", "fix")]
    [InlineData("../x", "t", "fix")]
    [InlineData("repos/repo", "", "fix")]
    [InlineData("repos/repo", "t", "Fix")]
    [InlineData("repos/repo", "t", "a..b")]
    public void Create_pr_blocks_bad_arguments(string clonePath, string title, string branch)
    {
        Directory.CreateDirectory(Path.Combine(_root, "repos", "repo"));
        Assert.IsType<ToolAssessment.Blocked>(_tools[BrokerTools.GitHubCreatePr].Assess(A(new { clonePath, title, branch })));
    }

    [Fact]
    public void Always_approve_comes_from_the_gateway_policy()
    {
        var gw = new RecordingGateway();
        var policy = new BrokerPolicy(new HashSet<string> { BrokerTools.DownloadToWorkspace }, Array.Empty<string>());
        var tools = AgentBrokerTools.All(gw, policy, _root).ToDictionary(t => t.Name);

        Assert.True(tools[BrokerTools.DownloadToWorkspace].AlwaysRequiresApproval);
        Assert.False(tools[BrokerTools.HttpFetch].AlwaysRequiresApproval);
    }

    [Fact]
    public async Task Policy_loader_reads_the_gateway_and_falls_back_to_conservative()
    {
        var gw = new RecordingGateway
        {
            Policy = new AgentToolPolicyResponse(new[] { "http_fetch" }, new[] { "sandbox_run" }, new[] { "x.org" }),
        };
        var loaded = await BrokerPolicyLoader.LoadAsync(gw, new ListLogger<object>(), default);
        Assert.Equal(new[] { "sandbox_run" }, loaded.AlwaysApprove);
        Assert.True(loaded.IsFetchAllowed("x.org"));

        var failing = new FailingPolicyGateway();
        var fallback = await BrokerPolicyLoader.LoadAsync(failing, new ListLogger<object>(), default, attempts: 1);
        Assert.Same(BrokerPolicy.Conservative, fallback);
        Assert.All(BrokerTools.All, t => Assert.Contains(t, fallback.AlwaysApprove));
    }

    private sealed class FailingPolicyGateway : IGatewayClient
    {
        public Task<AgentToolPolicyResponse> GetToolPolicyAsync(CancellationToken ct) => throw new HttpRequestException("down");
        public Task ReportSkillsAsync(IReadOnlyList<AgentSkillDto> skills, CancellationToken ct) => throw new NotSupportedException();
        public Task<ToolResult> InvokeToolAsync(string tool, InvokeToolRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<InboxResult> PollInboxAsync(int waitSeconds, CancellationToken ct) => throw new NotSupportedException();
        public Task<ContextResult> GetContextAsync(Guid threadId, int reserveTokens, CancellationToken ct) => throw new NotSupportedException();
        public Task<ChatOutcome> StreamChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyCollection<IAgentTool> tools, Guid correlationId, Guid threadId, CancellationToken ct) => throw new NotSupportedException();
        public Task PostReplyAsync(Guid threadId, string content, Guid correlationId, IReadOnlyList<ToolExchangeDto> exchanges, CancellationToken ct) => throw new NotSupportedException();
        public Task FailAsync(Guid messageId, string reason, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApprovalDto> RequestApprovalAsync(CreateApprovalRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApprovalDto> PollApprovalAsync(Guid approvalId, int waitSeconds, CancellationToken ct) => throw new NotSupportedException();
        public Task ReportToolCallAsync(ToolCallReportRequest report, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> ClassifyRiskAsync(IReadOnlyList<ChatMessage> messages, Guid correlationId, Guid threadId, CancellationToken ct) => throw new NotSupportedException();
    }
}
