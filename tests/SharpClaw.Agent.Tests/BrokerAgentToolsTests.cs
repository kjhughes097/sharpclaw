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
