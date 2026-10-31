using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Tests;

/// <summary>Context management (pre-3.3 spec section 6).</summary>
public class ContextTests
{
    // ---- unit: estimation and selection ----

    private static readonly ContextOptions Options = new()
    {
        DefaultWindowTokens = 1000, ReplyReserveTokens = 100, CharsPerToken = 4, MessageOverheadTokens = 4,
    };

    [Fact]
    public void Estimate_is_ceil_of_chars_over_chars_per_token_plus_overhead()
    {
        var estimator = new TokenEstimator(Options);
        Assert.Equal(4, estimator.Estimate(new ContextMessage(Roles.User, "")));
        Assert.Equal(5, estimator.Estimate(new ContextMessage(Roles.User, "a")));
        Assert.Equal(5, estimator.Estimate(new ContextMessage(Roles.User, "abcd")));
        Assert.Equal(6, estimator.Estimate(new ContextMessage(Roles.User, "abcde")));

        var call = new ContextToolCall("c", "function", new ContextFunction("t", "{}"));
        var json = System.Text.Json.JsonSerializer.Serialize(new[] { call }, SharpClawJson.Options);
        Assert.Equal(ContextDefaults.Tokens(json.Length) + 4,
            estimator.Estimate(new ContextMessage(Roles.Assistant, null, new[] { call })));
    }

    [Fact]
    public void The_agents_turn_budget_estimates_exactly_like_the_gateway()
    {
        // The agent compacts a turn to fit what the gateway's preflight will accept: they must agree.
        var messages = new[]
        {
            new ChatMessage(Roles.System, "soul"),
            new ChatMessage(Roles.User, "hello é"),
            new ChatMessage(Roles.Assistant, null, new[] { new ChatToolCall("call_1", "http_fetch", """{"url":"https://x.test/"}""") }),
            new ChatMessage(Roles.Tool, new string('a', 1001), ToolCallId: "call_1"),
        };
        var gateway = new TokenEstimator(new ContextOptions()).Estimate(messages.Select(m => new ContextMessage(m.Role, m.Content,
            m.ToolCalls?.Select(c => new ContextToolCall(c.Id, "function", new ContextFunction(c.Name, c.Arguments))).ToList(),
            m.ToolCallId)));

        Assert.Equal(gateway, SharpClaw.Agent.TurnBudget.Estimate(messages, schemaChars: 0));
        Assert.Equal(SharpClaw.Agent.TurnBudget.ReplyReserveTokens, new ContextOptions().ReplyReserveTokens);
    }

    /// <summary>A thread of turns, each a user message of <c>chars</c> characters plus a reply.</summary>
    private static (List<Message> Rows, List<Guid> UserIds) Thread(params int[] chars)
    {
        var rows = new List<Message>();
        var ids = new List<Guid>();
        var seq = 0;
        foreach (var n in chars)
        {
            var user = new Message { Id = Guid.NewGuid(), Role = Roles.User, Kind = MessageKinds.Message, Content = new string('u', n), Seq = ++seq };
            rows.Add(user);
            ids.Add(user.Id);
            rows.Add(new Message { Id = Guid.NewGuid(), Role = Roles.Assistant, Kind = MessageKinds.Message, Content = "ok!!", CorrelationId = user.Id, Seq = ++seq });
        }
        return (rows, ids);
    }

    private static Task<ContextBuilder.Result> BuildAsync(
        List<Message> rows, Guid current, int window = 1000, int reserve = 0, IContextSummariser? summariser = null) =>
        new ContextBuilder(Options, summariser ?? new NoSummariser()).BuildAsync(Guid.NewGuid(), rows, current, window, reserve, default);

    [Fact]
    public async Task Everything_fits_so_nothing_is_dropped()
    {
        var (rows, ids) = Thread(40, 40, 40);
        var result = await BuildAsync(rows, ids[^1]);

        Assert.False(result.TooLarge);
        Assert.Equal(0, result.DroppedTurns);
        // The current turn's reply doesn't exist yet in real use, but its user row is included.
        Assert.Equal(6, result.Messages.Count);
        Assert.Equal(new TokenEstimator(Options).Estimate(result.Messages), result.EstimatedTokens);
    }

    [Fact]
    public async Task Oldest_whole_turns_are_dropped_first_and_a_note_is_added()
    {
        // Each turn: user (400 chars → 104 tokens) + reply (5 tokens) = 109. Budget 1000 − 100 − 600 = 300.
        var (rows, ids) = Thread(400, 400, 400, 400);
        var result = await BuildAsync(rows, ids[^1], reserve: 600);

        Assert.Equal(2, result.DroppedTurns);
        Assert.Equal(Roles.System, result.Messages[0].Role);
        Assert.Equal("Earlier conversation (2 turns) is not shown.", result.Messages[0].Content);
        Assert.Equal(5, result.Messages.Count);
        Assert.Equal(new string('u', 400), result.Messages[1].Content);
    }

    [Fact]
    public async Task The_note_is_budgeted_so_the_result_never_exceeds_the_budget()
    {
        // Budget 1000 − 100 − 675 = 225: two turns (218) fit, but not with the note (+15), so one more goes.
        var (rows, ids) = Thread(400, 400, 400, 400);
        var result = await BuildAsync(rows, ids[^1], reserve: 675);

        Assert.Equal(3, result.DroppedTurns);
        Assert.True(result.EstimatedTokens <= 225, $"estimated {result.EstimatedTokens}");
        Assert.Equal(new TokenEstimator(Options).Estimate(result.Messages), result.EstimatedTokens);
    }

    [Fact]
    public async Task Reserve_shifts_the_budget()
    {
        var (rows, ids) = Thread(400, 400, 400, 400);
        Assert.Equal(0, (await BuildAsync(rows, ids[^1], reserve: 0)).DroppedTurns);
        Assert.Equal(2, (await BuildAsync(rows, ids[^1], reserve: 600)).DroppedTurns);
        Assert.Equal(3, (await BuildAsync(rows, ids[^1], reserve: 700)).DroppedTurns);
    }

    [Fact]
    public async Task Selection_stops_at_the_first_turn_that_does_not_fit()
    {
        // Newest-first: turn 3 (big) doesn't fit, so turn 2 (small) isn't added either.
        var (rows, ids) = Thread(40, 40, 3000, 40);
        var result = await BuildAsync(rows, ids[^1], window: 800);

        Assert.Equal(3, result.DroppedTurns);
        Assert.Equal(3, result.Messages.Count);
    }

    [Fact]
    public async Task Current_turn_that_alone_exceeds_the_budget_is_too_large()
    {
        var (rows, ids) = Thread(40, 4000);
        Assert.True((await BuildAsync(rows, ids[^1])).TooLarge);
    }

    [Fact]
    public async Task Later_queued_turns_are_not_included()
    {
        var (rows, ids) = Thread(40, 40, 40);
        var result = await BuildAsync(rows, ids[1]);
        Assert.Equal(4, result.Messages.Count);
    }

    [Fact]
    public async Task Turns_are_grouped_by_correlation_even_when_interleaved()
    {
        // A second user message was queued (seq 2) while turn 1 ran; turn 1's exchange and reply come after it.
        var u1 = new Message { Id = Guid.NewGuid(), Role = Roles.User, Kind = MessageKinds.Message, Content = "first", Seq = 1 };
        var u2 = new Message { Id = Guid.NewGuid(), Role = Roles.User, Kind = MessageKinds.Message, Content = "second", Seq = 2 };
        var rows = new List<Message>
        {
            u1, u2,
            new() { Role = Roles.Assistant, Kind = MessageKinds.ToolRequest, Content = "", CorrelationId = u1.Id, Seq = 3,
                ToolCalls = """[{"id":"c1","name":"workspace_list","arguments":"{}"}]""" },
            new() { Role = Roles.Tool, Kind = MessageKinds.ToolResult, Content = "{\"ok\":true}", ToolCallId = "c1", CorrelationId = u1.Id, Seq = 4 },
            new() { Role = Roles.Assistant, Kind = MessageKinds.Message, Content = "reply 1", CorrelationId = u1.Id, Seq = 5 },
        };

        var result = await BuildAsync(rows, u2.Id);

        Assert.Equal(new[] { Roles.User, Roles.Assistant, Roles.Tool, Roles.Assistant, Roles.User }, result.Messages.Select(m => m.Role));
        Assert.Equal("second", result.Messages[^1].Content);
        Assert.Equal("c1", result.Messages[2].ToolCallId);
    }

    [Fact]
    public async Task Untrusted_stubs_are_applied_before_estimation()
    {
        var u1 = new Message { Id = Guid.NewGuid(), Role = Roles.User, Kind = MessageKinds.Message, Content = "read it", Seq = 1 };
        var u2 = new Message { Id = Guid.NewGuid(), Role = Roles.User, Kind = MessageKinds.Message, Content = "next", Seq = 5 };
        var rows = new List<Message>
        {
            u1,
            new() { Role = Roles.Assistant, Kind = MessageKinds.ToolRequest, Content = "", CorrelationId = u1.Id, Seq = 2,
                ToolCalls = """[{"id":"c1","name":"workspace_read","arguments":"{}"}]""" },
            new() { Role = Roles.Tool, Kind = MessageKinds.ToolResult, Content = new string('x', 3000), ToolCallId = "c1",
                CorrelationId = u1.Id, Untrusted = true, Seq = 3 },
            new() { Role = Roles.Assistant, Kind = MessageKinds.Message, Content = "done", CorrelationId = u1.Id, Seq = 4 },
            u2,
        };

        // 3000 raw chars would be 750 tokens and not fit 1000 − 100 − 300; the stub does.
        var result = await BuildAsync(rows, u2.Id, reserve: 300);

        Assert.Equal(0, result.DroppedTurns);
        Assert.Equal(ModelReplay.UntrustedStub, result.Messages[2].Content);
    }

    private sealed class FixedSummariser(string? summary) : IContextSummariser
    {
        public int DroppedCount { get; private set; }

        public Task<string?> SummariseAsync(Guid threadId, IReadOnlyList<ContextMessage> droppedMessages, CancellationToken ct)
        {
            DroppedCount = droppedMessages.Count;
            return Task.FromResult(summary);
        }
    }

    [Fact]
    public async Task A_summary_from_the_seam_replaces_the_generic_note()
    {
        var (rows, ids) = Thread(400, 400, 400, 400);
        var summariser = new FixedSummariser("Summary: the user asked about turtles.");

        var result = await BuildAsync(rows, ids[^1], reserve: 600, summariser: summariser);

        Assert.Equal("Summary: the user asked about turtles.", result.Messages[0].Content);
        Assert.Equal(4, summariser.DroppedCount);
    }

    // ---- integration: endpoint, agent window, preflight ----

    private sealed record Setup(GatewayFixture Fx, AgentDto Agent, string Token, Guid Thread);

    private static async Task<Setup> SetupAsync(int? window = null, IDictionary<string, string?>? config = null)
    {
        var fx = await GatewayFixture.CreateAsync(extraConfig: config);
        using var admin = fx.Admin();
        var created = await (await admin.PostAsJsonAsync("/api/agents",
            new CreateAgentRequest("assistant", "test/model", 10, window), SharpClawJson.Options))
            .Content.ReadFromJsonAsync<CreatedAgentResponse>(SharpClawJson.Options);
        var thread = await fx.CreateThreadAsync(created!.Agent.Id);
        return new Setup(fx, created.Agent, created.Token.Value, thread);
    }

    private static async Task<HttpResponseMessage> GetContextAsync(Setup s, int reserve = 0, string? token = null)
    {
        using var agent = s.Fx.AgentClient(token ?? s.Token);
        return await agent.GetAsync($"/agent/threads/{s.Thread}/context?reserveTokens={reserve}");
    }

    [Fact]
    public async Task Context_endpoint_uses_the_agent_window_and_updates_the_thread()
    {
        var s = await SetupAsync(window: 3000);
        await using var _ = s.Fx;
        for (var i = 0; i < 4; i++)
        {
            var corr = await s.Fx.PostUserMessageAsync(s.Thread, new string('q', 4000));
            await s.Fx.ClaimAsync(s.Token);
            using var agent = s.Fx.AgentClient(s.Token);
            (await agent.PostAsJsonAsync($"/agent/threads/{s.Thread}/messages", new AgentReplyRequest("ok", corr), SharpClawJson.Options))
                .EnsureSuccessStatusCode();
        }
        await s.Fx.PostUserMessageAsync(s.Thread, "latest");
        await s.Fx.ClaimAsync(s.Token);

        var resp = await GetContextAsync(s, reserve: 200);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = (await resp.Content.ReadFromJsonAsync<ContextResponse>(SharpClawJson.Options))!;

        // Budget 3000 − 200 − 2000 = 800: only the latest turn and the note fit.
        Assert.Equal(3000, body.WindowTokens);
        Assert.True(body.Truncated);
        Assert.Equal(4, body.DroppedTurns);
        Assert.Equal(new[] { Roles.System, Roles.User }, body.Messages.Select(m => m.Role));
        Assert.Equal("latest", body.Messages[^1].Content);

        using var admin = s.Fx.Admin();
        var detail = await admin.GetFromJsonAsync<ThreadDetailResponse>($"/api/threads/{s.Thread}", SharpClawJson.Options);
        var context = detail!.Thread.Context!;
        Assert.Equal(4, context.DroppedTurns);
        Assert.Equal(body.EstimatedTokens, context.EstimatedTokens);
        Assert.Equal(3000, context.WindowTokens);
    }

    [Fact]
    public async Task Context_endpoint_rejects_an_oversize_turn_and_bad_requests()
    {
        var s = await SetupAsync(config: new Dictionary<string, string?> { ["Context:DefaultWindowTokens"] = "2500" });
        await using var _ = s.Fx;
        await s.Fx.PostUserMessageAsync(s.Thread, new string('q', 4000));
        await s.Fx.ClaimAsync(s.Token);

        var tooLarge = await GetContextAsync(s);
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        Assert.Equal(ErrorCodes.ContextTooLarge,
            (await tooLarge.Content.ReadFromJsonAsync<ApiErrorResponse>(SharpClawJson.Options))!.Error.Code);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetContextAsync(s, reserve: -1)).StatusCode);

        var (_, otherToken) = await s.Fx.CreateAgentAsync("other");
        Assert.Equal(HttpStatusCode.NotFound, (await GetContextAsync(s, token: otherToken)).StatusCode);
    }

    [Fact]
    public async Task Agent_window_is_validated_and_exposed()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        using var admin = fx.Admin();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/agents",
            new CreateAgentRequest("a", "m", 1, 999), SharpClawJson.Options)).StatusCode);
        var (agent, _) = await fx.CreateAgentAsync();
        Assert.Null(agent.ContextWindowTokens);

        var patched = await (await admin.PatchAsJsonAsync($"/api/agents/{agent.Name}",
            new UpdateAgentRequest(null, null, null, 128000), SharpClawJson.Options)).Content.ReadFromJsonAsync<AgentDto>(SharpClawJson.Options);
        Assert.Equal(128000, patched!.ContextWindowTokens);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsJsonAsync($"/api/agents/{agent.Name}",
            new UpdateAgentRequest(null, null, null, 2_000_001), SharpClawJson.Options)).StatusCode);
    }

    [Fact]
    public async Task Chat_preflight_rejects_an_oversize_request_and_records_the_estimate()
    {
        var s = await SetupAsync(window: 3000);
        await using var _ = s.Fx;
        var corr = await s.Fx.PostUserMessageAsync(s.Thread, "hi");
        await s.Fx.ClaimAsync(s.Token);

        var ok = await s.Fx.ChatAsync(s.Token, corr, s.Thread, "hello");
        await ok.Content.ReadAsStringAsync();
        var big = await s.Fx.ChatAsync(s.Token, corr, s.Thread, new string('x', 6000));

        Assert.Equal(HttpStatusCode.BadRequest, big.StatusCode);
        Assert.Equal(ErrorCodes.ContextTooLarge, (await big.Content.ReadFromJsonAsync<ApiErrorResponse>(SharpClawJson.Options))!.Error.Code);
        using var db = s.Fx.NewDb();
        var calls = await db.LlmCalls.AsNoTracking().OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync();
        Assert.Equal(2, calls.Count);
        Assert.Equal(LlmCallStatus.Ok, calls[0].Status);
        Assert.Equal(ContextDefaults.Tokens(5) + 4, calls[0].EstimatedPromptTokens);
        Assert.Equal(LlmCallStatus.Rejected, calls[1].Status);
        Assert.Equal(ErrorCodes.ContextTooLarge, calls[1].ErrorCode);
        Assert.Equal(1504, calls[1].EstimatedPromptTokens);
    }

    [Fact]
    public async Task Provider_context_length_error_maps_to_context_too_large()
    {
        var script = ChatTestHelpers.WriteScript("""{ "rules": [ { "match": "huge", "error": { "type": "context_length" } } ] }""");
        var s = await SetupAsync(config: new Dictionary<string, string?> { ["Fake:ScriptPath"] = script });
        await using var _ = s.Fx;
        var corr = await s.Fx.PostUserMessageAsync(s.Thread, "huge");
        await s.Fx.ClaimAsync(s.Token);

        var resp = await s.Fx.ChatAsync(s.Token, corr, s.Thread, "huge");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        using var db = s.Fx.NewDb();
        Assert.Equal(ErrorCodes.ContextTooLarge, (await db.LlmCalls.AsNoTracking().SingleAsync()).ErrorCode);
    }
}
