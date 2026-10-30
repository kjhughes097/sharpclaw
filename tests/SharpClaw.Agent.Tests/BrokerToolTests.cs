using System.Net;
using System.Text;
using System.Text.Json;
using SharpClaw.Agent.Tools;
using SharpClaw.Agent.Tools.Broker;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Tests;

/// <summary>
/// The agent half of the broker (external tools spec section 4): broker tools run through the
/// dispatcher like any other, pass the call's identity and approval to the gateway, and approvals
/// carry the arguments hash.
/// </summary>
public class BrokerToolTests
{
    private sealed class TestBrokerTool(IGatewayClient gateway, bool alwaysApprove = false)
        : BrokerTool(gateway, new BrokerPolicy(alwaysApprove ? new HashSet<string> { "sandbox_run" } : new HashSet<string>(), Array.Empty<string>()))
    {
        public override string Name => "sandbox_run";
        public override string Description => "test";
        public override string ParametersJson => """{"type":"object"}""";
        public override bool ReturnsUntrustedContent => true;
        public override ToolAssessment Assess(JsonElement args) => new ToolAssessment.Scored(RiskLevel.Low, "test");
        public override string Summarize(JsonElement args) => "run";
        public override IReadOnlyDictionary<string, string>? ClassifierFields(JsonElement args) => null;
    }

    private static (ToolDispatcher, RecordingGateway) Create(bool alwaysApprove)
    {
        var gateway = new RecordingGateway();
        var tool = new TestBrokerTool(gateway, alwaysApprove);
        var config = new AgentToolConfig(RiskLevel.Critical, false, 10, new[] { tool.Name });
        return (new ToolDispatcher(new ToolRegistry(new[] { tool }, config.Tools), config, gateway, new ListLogger<ToolDispatcher>()), gateway);
    }

    [Fact]
    public async Task Passes_the_turn_call_id_and_arguments_to_the_gateway()
    {
        var (d, gw) = Create(alwaysApprove: false);
        var turn = new TurnContext(Guid.NewGuid(), Guid.NewGuid());

        var result = await d.DispatchAsync(new ToolCallRequest("call_9", "sandbox_run", """{"command":"ls"}"""), turn, default);

        Assert.True(result.Ok);
        Assert.True(result.Untrusted);
        var (tool, request) = Assert.Single(gw.ToolInvocations);
        Assert.Equal("sandbox_run", tool);
        Assert.Equal((turn.CorrelationId, turn.ThreadId, "call_9", (Guid?)null),
            (request.CorrelationId, request.ThreadId, request.ToolCallId, request.ApprovalId));
        Assert.Equal("ls", request.Arguments.GetProperty("command").GetString());
    }

    [Fact]
    public async Task An_approved_call_sends_the_approval_id_and_the_approval_carries_the_args_hash()
    {
        var (d, gw) = Create(alwaysApprove: true);
        var approved = RecordingGateway.Approval(ApprovalStatuses.Approved, ApprovalDecidedBy.User);
        gw.ApprovalReplies.Enqueue(approved);
        const string args = """{ "cwd": "repo", "command": "npm test" }""";

        await d.DispatchAsync(new ToolCallRequest("call_1", "sandbox_run", args), new TurnContext(Guid.NewGuid(), Guid.NewGuid()), default);

        var request = Assert.Single(gw.ApprovalRequests);
        Assert.Equal(ToolArgsHash.Compute(JsonDocument.Parse(args).RootElement), request.ArgsHash);
        Assert.Equal("npm test", request.Arguments!.Value.GetProperty("command").GetString());
        Assert.Equal(approved.Id, Assert.Single(gw.ToolInvocations).Request.ApprovalId);
    }

    [Fact]
    public async Task A_refused_broker_call_becomes_an_error_result()
    {
        var (d, gw) = Create(alwaysApprove: false);
        gw.ToolReply = (_, _) => ToolResult.Error(ErrorCodes.ApprovalRequired, "needs approval");

        var result = await d.DispatchAsync(new ToolCallRequest("c", "sandbox_run", "{}"), new TurnContext(Guid.NewGuid(), Guid.NewGuid()), default);

        Assert.False(result.Ok);
        Assert.False(result.Untrusted);
        Assert.Equal(ToolCallStatuses.Failed, Assert.Single(gw.Reports).Status);
    }

    [Fact]
    public async Task A_gateway_refusal_of_the_approval_reaches_the_model_as_blocked()
    {
        var (d, gw) = Create(alwaysApprove: true);
        gw.ApprovalException = new ApprovalRefusedException("blocked_path", ".github/workflows/ci.yml matches a blocked pattern.");

        var result = await d.DispatchAsync(new ToolCallRequest("c", "sandbox_run", "{}"), new TurnContext(Guid.NewGuid(), Guid.NewGuid()), default);

        var json = JsonDocument.Parse(result.Content).RootElement;
        Assert.Equal("blocked_path", json.GetProperty("error").GetString());
        Assert.Contains("blocked pattern", json.GetProperty("message").GetString());
        Assert.Empty(gw.ToolInvocations);
        var report = Assert.Single(gw.Reports);
        Assert.Equal((ToolCallStatuses.Blocked, ToolDecisions.Blocked), (report.Status, report.Decision));
    }

    [Fact]
    public async Task Http_client_turns_a_403_approval_answer_into_a_refusal()
    {
        var client = Client(HttpStatusCode.Forbidden, """{"error":{"code":"symlink_not_allowed","message":"link is a symbolic link."}}""");

        var ex = await Assert.ThrowsAsync<ApprovalRefusedException>(() => client.RequestApprovalAsync(
            new CreateApprovalRequest(Guid.NewGuid(), Guid.NewGuid(), "c", "github_create_pr", "pr", "high", "r"), default));

        Assert.Equal("symlink_not_allowed", ex.Code);
    }

    // ----- GatewayHttpClient.InvokeToolAsync -----

    private static GatewayHttpClient Client(HttpStatusCode status, string body, List<HttpRequestMessage>? seen = null) =>
        new(new HttpClient(new Stub(r =>
        {
            seen?.Add(r);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        })), new AgentConfig { GatewayUrl = "http://gw:8081", AgentToken = "sc_x" });

    private static InvokeToolRequest Request() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "c", JsonDocument.Parse("""{"url":"https://x"}""").RootElement, null);

    [Fact]
    public async Task Http_client_posts_to_the_tool_route_and_returns_the_result_as_is()
    {
        var seen = new List<HttpRequestMessage>();
        var client = Client(HttpStatusCode.OK, """{"ok":true,"content":"hi"}""", seen);

        var result = await client.InvokeToolAsync("http_fetch", Request(), default);

        Assert.True(result.Ok);
        Assert.Equal("""{"ok":true,"content":"hi"}""", result.Json);
        Assert.Equal("http://gw:8081/agent/tools/http_fetch", Assert.Single(seen).RequestUri!.ToString());
    }

    [Fact]
    public async Task Http_client_maps_a_tool_error_and_a_gateway_refusal()
    {
        var toolError = await Client(HttpStatusCode.OK, """{"ok":false,"error":"http_404","message":"m"}""")
            .InvokeToolAsync("http_fetch", Request(), default);
        var refusal = await Client(HttpStatusCode.Forbidden, """{"error":{"code":"approval_required","message":"needs approval"}}""")
            .InvokeToolAsync("http_fetch", Request(), default);
        var garbage = await Client(HttpStatusCode.BadGateway, "<html>")
            .InvokeToolAsync("http_fetch", Request(), default);

        Assert.Equal((false, "http_404"), (toolError.Ok, toolError.ErrorCode));
        Assert.Equal((false, "approval_required"), (refusal.Ok, refusal.ErrorCode));
        Assert.Equal((false, ErrorCodes.ToolFailed), (garbage.Ok, garbage.ErrorCode));
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
