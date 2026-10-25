using SharpClaw.Gateway.Providers;

namespace SharpClaw.Gateway.Tests;

public class FakeProviderTests
{
    private static ProviderChatRequest Req(string lastUser) =>
        new("test/model", new[] { new ProviderChatMessage("user", lastUser) });

    private static async Task<List<ChatStreamEvent>> Collect(IChatProvider provider, ProviderChatRequest req)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var e in provider.StreamAsync(req, CancellationToken.None))
            events.Add(e);
        return events;
    }

    [Fact]
    public async Task Default_echo_streams_and_reports_usage()
    {
        var provider = new FakeProvider(FakeScript.Empty, 0.000001);
        var events = await Collect(provider, Req("hello"));

        var text = string.Concat(events.OfType<ChatStreamEvent.Delta>().Select(d => d.Text));
        Assert.Equal("Fake reply to: hello", text);
        Assert.Contains(events, e => e is ChatStreamEvent.Usage u && u.PromptTokens > 0 && u.CompletionTokens > 0);
        Assert.IsType<ChatStreamEvent.Done>(events[^1]);
    }

    [Fact]
    public async Task Default_chunks_are_eight_chars()
    {
        var provider = new FakeProvider(FakeScript.Empty, 0.000001);
        var events = await Collect(provider, Req("x"));
        var first = events.OfType<ChatStreamEvent.Delta>().First();
        Assert.Equal(8, first.Text.Length);
    }

    [Fact]
    public async Task Script_rule_matches_and_overrides_usage()
    {
        var script = FakeScript.Parse("""
        { "rules": [
          { "match": "^special$", "response": "custom", "chunkSize": 2,
            "usage": { "promptTokens": 100, "completionTokens": 50, "costUsd": 0.5 } }
        ] }
        """);
        var provider = new FakeProvider(script, 0.000001);
        var events = await Collect(provider, Req("special"));

        var text = string.Concat(events.OfType<ChatStreamEvent.Delta>().Select(d => d.Text));
        Assert.Equal("custom", text);
        var usage = events.OfType<ChatStreamEvent.Usage>().Single();
        Assert.Equal(100, usage.PromptTokens);
        Assert.Equal(50, usage.CompletionTokens);
        Assert.Equal(0.5, usage.CostUsd);
    }

    [Fact]
    public async Task Script_usage_null_reports_no_usage()
    {
        var script = FakeScript.Parse("""
        { "rules": [ { "match": ".*", "response": "hi", "usage": null } ] }
        """);
        var provider = new FakeProvider(script, 0.000001);
        var events = await Collect(provider, Req("anything"));
        Assert.DoesNotContain(events, e => e is ChatStreamEvent.Usage);
    }

    [Fact]
    public async Task Script_http_error_throws_upstream()
    {
        var script = FakeScript.Parse("""
        { "rules": [ { "match": "^fail-http$", "error": { "type": "http", "status": 500 } } ] }
        """);
        var provider = new FakeProvider(script, 0.000001);
        var ex = await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(Req("fail-http"), CancellationToken.None)) { }
        });
        Assert.Equal(ProviderErrorKind.Upstream, ex.Kind);
        Assert.Equal(500, ex.HttpStatus);
    }

    [Fact]
    public async Task Script_timeout_error_throws_timeout()
    {
        var script = FakeScript.Parse("""
        { "rules": [ { "match": "^fail-timeout$", "error": { "type": "timeout" } } ] }
        """);
        var provider = new FakeProvider(script, 0.000001);
        var ex = await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(Req("fail-timeout"), CancellationToken.None)) { }
        });
        Assert.Equal(ProviderErrorKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task Script_midstream_error_yields_then_throws()
    {
        var script = FakeScript.Parse("""
        { "rules": [ { "match": "^fail-midstream$", "response": "partial", "error": { "type": "midstream", "afterChars": 4 } } ] }
        """);
        var provider = new FakeProvider(script, 0.000001);
        var collected = new List<string>();
        await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var e in provider.StreamAsync(Req("fail-midstream"), CancellationToken.None))
                if (e is ChatStreamEvent.Delta d) collected.Add(d.Text);
        });
        Assert.Equal("part", string.Concat(collected));
    }

    [Fact]
    public async Task Script_rule_can_return_tool_calls()
    {
        var script = FakeScript.Parse("""
        { "rules": [ { "match": "^do it$", "toolCalls": [ { "name": "workspace_read", "arguments": { "path": "a.md" } } ] } ] }
        """);
        var events = await Collect(new FakeProvider(script, 0.000001), Req("do it"));

        Assert.Empty(events.OfType<ChatStreamEvent.Delta>());
        var call = Assert.Single(events.OfType<ChatStreamEvent.ToolCall>());
        Assert.Equal("workspace_read", call.Name);
        Assert.Equal("""{"path":"a.md"}""", call.ArgumentsJson);
        Assert.StartsWith("call_", call.Id);
        Assert.Equal("tool_calls", Assert.IsType<ChatStreamEvent.Done>(events[^1]).FinishReason);
    }

    [Fact]
    public async Task Rules_match_the_last_message_and_can_filter_by_role()
    {
        var script = FakeScript.Parse("""
        { "rules": [ { "match": "ok", "role": "tool", "response": "after tool" } ] }
        """);
        var provider = new FakeProvider(script, 0.000001);

        // Last message is a user message containing "ok": the tool-only rule must not match.
        var userOnly = await Collect(provider, Req("ok"));
        Assert.Equal("Fake reply to: ok", string.Concat(userOnly.OfType<ChatStreamEvent.Delta>().Select(d => d.Text)));

        var withTool = new ProviderChatRequest("m", new[]
        {
            new ProviderChatMessage("user", "go"),
            new ProviderChatMessage("assistant", null, new[] { new ProviderToolCall("c1", "t", "{}") }),
            new ProviderChatMessage("tool", "{\"ok\":true}", ToolCallId: "c1"),
        });
        var afterTool = await Collect(provider, withTool);
        Assert.Equal("after tool", string.Concat(afterTool.OfType<ChatStreamEvent.Delta>().Select(d => d.Text)));
    }
}
