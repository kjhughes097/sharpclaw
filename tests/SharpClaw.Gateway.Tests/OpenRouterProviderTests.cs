using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Providers;

namespace SharpClaw.Gateway.Tests;

/// <summary>
/// OpenRouterProvider against a fake HttpMessageHandler serving canned upstream SSE, so the parsing,
/// error and timeout paths are covered without network access.
/// </summary>
public class OpenRouterProviderTests
{
    private static ProviderChatRequest Request() => new(
        "openai/gpt-4o-mini",
        new[] { new ProviderChatMessage("system", "be brief"), new ProviderChatMessage("user", "hi") },
        Temperature: 0.2, MaxTokens: 50, TopP: 0.9, Stop: new[] { "END" });

    private static OpenRouterProvider Provider(FakeHandler handler, int connectTimeout = 10, int idleTimeout = 60) =>
        new(new HttpClient(handler),
            Options.Create(new OpenRouterOptions { BaseUrl = "https://openrouter.test/api/v1", ApiKey = "or-key" }),
            Options.Create(new ProviderOptions { ConnectTimeoutSeconds = connectTimeout, IdleTimeoutSeconds = idleTimeout }));

    private static string Sse(params string[] dataLines) =>
        string.Concat(dataLines.Select(d => d.StartsWith(':') ? d + "\n\n" : $"data: {d}\n\n"));

    private static async Task<List<ChatStreamEvent>> Collect(IChatProvider provider, CancellationToken ct = default)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var e in provider.StreamAsync(Request(), ct))
            events.Add(e);
        return events;
    }

    [Fact]
    public async Task Streams_deltas_then_usage_then_done()
    {
        var handler = FakeHandler.Ok(Sse(
            ": OPENROUTER PROCESSING",
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":"Hel"},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"content":"lo"},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15,"cost":0.00042}}""",
            "[DONE]"));

        var events = await Collect(Provider(handler));

        Assert.Equal(new[] { "Hel", "lo" }, events.OfType<ChatStreamEvent.Delta>().Select(d => d.Text));
        var usage = Assert.Single(events.OfType<ChatStreamEvent.Usage>());
        Assert.Equal(12, usage.PromptTokens);
        Assert.Equal(3, usage.CompletionTokens);
        Assert.Equal(0.00042, usage.CostUsd);
        var done = Assert.IsType<ChatStreamEvent.Done>(events[^1]);
        Assert.Equal("stop", done.FinishReason);
    }

    [Fact]
    public async Task Missing_usage_yields_no_usage_event()
    {
        var handler = FakeHandler.Ok(Sse(
            """{"choices":[{"index":0,"delta":{"content":"hi"},"finish_reason":"stop"}]}""",
            "[DONE]"));

        var events = await Collect(Provider(handler));

        Assert.DoesNotContain(events, e => e is ChatStreamEvent.Usage);
        Assert.IsType<ChatStreamEvent.Done>(events[^1]);
    }

    [Fact]
    public async Task Usage_without_cost_has_null_cost()
    {
        var handler = FakeHandler.Ok(Sse(
            """{"choices":[],"usage":{"prompt_tokens":5,"completion_tokens":1}}""",
            "[DONE]"));

        var usage = Assert.Single((await Collect(Provider(handler))).OfType<ChatStreamEvent.Usage>());
        Assert.Null(usage.CostUsd);
    }

    [Fact]
    public async Task Error_chunk_mid_stream_throws_upstream_after_earlier_deltas()
    {
        var handler = FakeHandler.Ok(Sse(
            """{"choices":[{"index":0,"delta":{"content":"par"},"finish_reason":null}]}""",
            """{"error":{"code":502,"message":"provider went away"}}"""));

        var deltas = new List<string>();
        var ex = await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var e in Provider(handler).StreamAsync(Request(), CancellationToken.None))
                if (e is ChatStreamEvent.Delta d) deltas.Add(d.Text);
        });

        Assert.Equal(ProviderErrorKind.Upstream, ex.Kind);
        Assert.Equal(new[] { "par" }, deltas);
    }

    [Fact]
    public async Task Non_success_status_throws_upstream_with_status_and_no_body()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":{"message":"secret upstream detail"}}"""),
        });

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Collect(Provider(handler)));

        Assert.Equal(ProviderErrorKind.Upstream, ex.Kind);
        Assert.Equal(401, ex.HttpStatus);
        Assert.DoesNotContain("secret upstream detail", ex.Message);
    }

    [Fact]
    public async Task Slow_response_headers_throw_timeout()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Collect(Provider(handler, connectTimeout: 1)));
        Assert.Equal(ProviderErrorKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task Stalled_stream_throws_idle_timeout()
    {
        var first = Sse("""{"choices":[{"index":0,"delta":{"content":"a"},"finish_reason":null}]}""");
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream(Encoding.UTF8.GetBytes(first))),
        });

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Collect(Provider(handler, idleTimeout: 1)));
        Assert.Equal(ProviderErrorKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task Request_has_auth_title_streaming_usage_and_sampling_fields()
    {
        var handler = FakeHandler.Ok(Sse("[DONE]"));
        await Collect(Provider(handler));

        var req = handler.LastRequest!;
        Assert.Equal("https://openrouter.test/api/v1/chat/completions", req.Uri.ToString());
        Assert.Equal("Bearer or-key", req.Authorization);
        Assert.Equal("SharpClaw", req.Title);

        using var body = JsonDocument.Parse(req.Body);
        var root = body.RootElement;
        Assert.Equal("openai/gpt-4o-mini", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.True(root.GetProperty("usage").GetProperty("include").GetBoolean());
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble());
        Assert.Equal(50, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.9, root.GetProperty("top_p").GetDouble());
        Assert.Equal("END", root.GetProperty("stop")[0].GetString());
        Assert.Equal(2, root.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Cancellation_is_not_reported_as_timeout()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(Provider(handler), cts.Token));
    }

    [Fact]
    public async Task Tool_call_fragments_are_accumulated_into_complete_calls()
    {
        var handler = FakeHandler.Ok(Sse(
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"workspace_read","arguments":""}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"pa"}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_b","function":{"name":"workspace_list","arguments":"{}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"a.md\"}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
            "[DONE]"));

        var events = await Collect(Provider(handler));

        var calls = events.OfType<ChatStreamEvent.ToolCall>().ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(new ChatStreamEvent.ToolCall("call_a", "workspace_read", """{"path":"a.md"}"""), calls[0]);
        Assert.Equal(new ChatStreamEvent.ToolCall("call_b", "workspace_list", "{}"), calls[1]);
        Assert.Equal("tool_calls", Assert.IsType<ChatStreamEvent.Done>(events[^1]).FinishReason);
    }

    [Fact]
    public async Task Tools_and_tool_messages_are_sent_upstream()
    {
        var handler = FakeHandler.Ok(Sse("[DONE]"));
        var request = new ProviderChatRequest("m", new[]
            {
                new ProviderChatMessage("user", "go"),
                new ProviderChatMessage("assistant", null, new[] { new ProviderToolCall("call_1", "workspace_read", "{\"path\":\"a\"}") }),
                new ProviderChatMessage("tool", "{\"ok\":true}", ToolCallId: "call_1"),
            },
            Tools: new[] { new ProviderTool("workspace_read", "Read a file", """{"type":"object"}""") },
            ToolChoice: "auto");

        await foreach (var _ in Provider(handler).StreamAsync(request, CancellationToken.None)) { }

        using var body = JsonDocument.Parse(handler.LastRequest!.Body);
        var root = body.RootElement;
        var tool = root.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("workspace_read", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("object", tool.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());

        var messages = root.GetProperty("messages");
        Assert.Equal(JsonValueKind.Null, messages[1].GetProperty("content").ValueKind);
        Assert.Equal("call_1", messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("{\"path\":\"a\"}", messages[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("call_1", messages[2].GetProperty("tool_call_id").GetString());
    }

    // ---- fakes ----

    private sealed record CapturedRequest(Uri Uri, string? Authorization, string? Title, string Body);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public CapturedRequest? LastRequest { get; private set; }

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((r, _) => Task.FromResult(respond(r))) { }

        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
            _respond = respond;

        public static FakeHandler Ok(string sse) => new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            LastRequest = new CapturedRequest(
                request.RequestUri!,
                request.Headers.TryGetValues("Authorization", out var a) ? a.Single() : null,
                request.Headers.TryGetValues("X-Title", out var t) ? t.Single() : null,
                body);
            return await _respond(request, ct);
        }
    }

    /// <summary>Returns some bytes, then blocks until cancelled (simulates a silent upstream).</summary>
    private sealed class StallingStream : Stream
    {
        private readonly byte[] _first;
        private bool _sent;

        public StallingStream(byte[] first) => _first = first;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_sent)
            {
                _sent = true;
                _first.CopyTo(buffer);
                return _first.Length;
            }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
