using System.Net;
using System.Text;
using System.Text.Json;
using SharpClaw.Agent.Tools;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Tests;

/// <summary>GatewayHttpClient's chat wire format: request payload and SSE parsing.</summary>
public class GatewayHttpClientTests
{
    private static AgentConfig Config => new() { GatewayUrl = "http://gw:8081", AgentToken = "sc_x" };

    [Fact]
    public async Task Stream_parser_collects_content_and_fragmented_tool_calls()
    {
        const string sse = """
            data: {"choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}

            data: {"choices":[{"index":0,"delta":{"content":"Working"},"finish_reason":null}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"workspace_read","arguments":"{\"pa"}}]},"finish_reason":null}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"a.md\"}"}}]},"finish_reason":null}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_b","type":"function","function":{"name":"workspace_list","arguments":""}}]},"finish_reason":null}]}

            data: {"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;

        var outcome = await GatewayHttpClient.ReadStreamAsync(new StringReader(sse), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal("Working", outcome.Content);
        Assert.Equal(new[]
        {
            new ChatToolCall("call_a", "workspace_read", """{"path":"a.md"}"""),
            new ChatToolCall("call_b", "workspace_list", "{}"),
        }, outcome.ToolCalls);
    }

    [Fact]
    public async Task Error_chunk_fails_as_provider_error()
    {
        const string sse = "data: {\"error\":{\"code\":\"provider_error\",\"message\":\"x\"}}\n\ndata: [DONE]\n\n";
        var outcome = await GatewayHttpClient.ReadStreamAsync(new StringReader(sse), CancellationToken.None);
        Assert.Equal("provider_error", outcome.FailCode);
    }

    [Fact]
    public void Payload_offers_tools_and_carries_tool_messages_without_a_model()
    {
        var tool = new FakeTool("workspace_read", new ToolAssessment.Scored(RiskLevel.Low, "x"));
        var messages = new[]
        {
            new ChatMessage(Roles.User, "read a.md"),
            new ChatMessage(Roles.Assistant, null, new[] { new ChatToolCall("call_1", "workspace_read", "{\"path\":\"a.md\"}") }),
            new ChatMessage(Roles.Tool, "{\"ok\":true}", ToolCallId: "call_1"),
        };

        var json = JsonSerializer.Serialize(GatewayHttpClient.BuildPayload(messages, new[] { tool }), SharpClawJson.Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("model", out _));
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());
        var fn = root.GetProperty("tools")[0].GetProperty("function");
        Assert.Equal("workspace_read", fn.GetProperty("name").GetString());
        Assert.Equal("object", fn.GetProperty("parameters").GetProperty("type").GetString());

        var wire = root.GetProperty("messages");
        Assert.Equal(JsonValueKind.Null, wire[1].GetProperty("content").ValueKind);
        Assert.Equal("call_1", wire[1].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("{\"path\":\"a.md\"}", wire[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("call_1", wire[2].GetProperty("tool_call_id").GetString());
        Assert.False(wire[0].TryGetProperty("tool_calls", out _));
    }

    [Fact]
    public void Payload_without_tools_omits_tools_and_tool_choice()
    {
        var json = JsonSerializer.Serialize(
            GatewayHttpClient.BuildPayload(new[] { new ChatMessage(Roles.User, "hi") }, Array.Empty<IAgentTool>()),
            SharpClawJson.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("tools", out _));
        Assert.False(doc.RootElement.TryGetProperty("tool_choice", out _));
    }

    [Theory]
    [InlineData("budget_exceeded")]
    [InlineData("loop_limit_exceeded")]
    public async Task Status_429_reports_the_gateway_error_code(string code)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":{\"code\":\"" + code + "\",\"message\":\"m\"}}", Encoding.UTF8, "application/json"),
        });
        var client = new GatewayHttpClient(new HttpClient(handler), Config);

        var outcome = await client.StreamChatAsync(new[] { new ChatMessage(Roles.User, "hi") }, Array.Empty<IAgentTool>(),
            Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(code, outcome.FailCode);
    }

    [Fact]
    public async Task Risk_classification_uses_the_risk_assessment_purpose()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"low\"}}]}\n\ndata: [DONE]\n\n"),
        });
        var client = new GatewayHttpClient(new HttpClient(handler), Config);

        var reply = await client.ClassifyRiskAsync(new[] { new ChatMessage(Roles.User, "x") }, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal("low", reply);
        Assert.Equal(LlmCallPurpose.RiskAssessment, handler.LastRequest!.Headers.GetValues("X-SharpClaw-Purpose").Single());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }
}
