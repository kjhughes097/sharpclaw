using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Providers;
using SharpClaw.Gateway.Services.Chat;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway.Services.Broker;

/// <summary>
/// <c>web_search</c> (external tools spec section 7.2), run in the gateway: a separate OpenRouter
/// call with the web plugin on <c>WebSearch:Model</c>, reduced to <c>{ title, url, excerpt }</c>
/// results. The call is recorded in <c>llm_calls</c> (purpose <c>web_search</c>) and counts
/// against the agent's daily budget. It runs as an ordinary tool call through the agent's
/// dispatcher; the provider's server-side search tool is never enabled on chat completions.
/// </summary>
public sealed class WebSearchToolHandler(
    SharpClawDbContext db,
    IWebSearchProvider provider,
    IOptions<WebSearchOptions> options,
    TimeProvider time,
    GatewayMetrics metrics,
    ILogger<WebSearchToolHandler> logger) : IBrokerToolHandler
{
    public const int MaxQueryChars = 400;
    public const int MaxResults = 10;

    public IReadOnlyCollection<string> Tools { get; } = [BrokerTools.WebSearch];

    public async Task<BrokerResult> InvokeAsync(BrokerInvocation call, CancellationToken ct)
    {
        var query = call.Arguments.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString()!.Trim() : "";
        if (query.Length is 0 or > MaxQueryChars)
            return BrokerResult.ToolError(ErrorCodes.InvalidArguments, $"query must be 1-{MaxQueryChars} characters.");
        var maxResults = 5;
        if (call.Arguments.TryGetProperty("maxResults", out var m) && m.ValueKind != JsonValueKind.Null &&
            (m.ValueKind != JsonValueKind.Number || !m.TryGetInt32(out maxResults) || maxResults is < 1 or > MaxResults))
            return BrokerResult.ToolError(ErrorCodes.InvalidArguments, $"maxResults must be an integer from 1 to {MaxResults}.");

        var model = options.Value.Model;
        var now = time.GetUtcNow().UtcDateTime;
        var recorder = new LlmCallRecorder(db, metrics, logger, call.Agent, call.ThreadId, call.CorrelationId, now,
            LlmCallPurpose.WebSearch, model);

        // Searches spend from the same daily budget as chat.
        var spent = await AgentQueries.TodaySpendAsync(db, time, call.Agent.Id, ct);
        if (spent >= call.Agent.DailyBudgetUsd)
        {
            metrics.RecordBudgetExceeded(call.Agent.Name);
            await recorder.RecordAsync(LlmCallOutcome.Rejected(ErrorCodes.BudgetExceeded), ct);
            return BrokerResult.Refused(429, ErrorCodes.BudgetExceeded, "Daily budget exhausted.");
        }

        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        WebSearchResponse response;
        try
        {
            response = await provider.SearchAsync(model, query, maxResults, timeout.Token);
        }
        catch (Exception ex) when (ex is ProviderException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            var code = ex is ProviderException { Kind: not ProviderErrorKind.Timeout } ? ErrorCodes.ProviderError : ErrorCodes.ProviderTimeout;
            await recorder.RecordAsync(new LlmCallOutcome
            {
                Status = LlmCallStatus.Error,
                LatencyMs = (int)sw.ElapsedMilliseconds,
                ErrorCode = code,
                ErrorDetail = (ex as ProviderException)?.HttpStatus?.ToString(),
            }, CancellationToken.None);
            return BrokerResult.Refused(502, ErrorCodes.ToolFailed, "The web search failed.", "openrouter.ai");
        }

        await recorder.RecordAsync(new LlmCallOutcome
        {
            Status = LlmCallStatus.Ok,
            PromptTokens = response.Usage?.PromptTokens ?? 0,
            CompletionTokens = response.Usage?.CompletionTokens ?? 0,
            CostUsd = response.Usage?.CostUsd ?? 0,
            UsageKnown = response.Usage?.CostUsd is not null,
            LatencyMs = (int)sw.ElapsedMilliseconds,
        }, CancellationToken.None);

        var results = response.Results.Select(r => new { title = r.Title, url = r.Url, excerpt = r.Excerpt }).ToList();
        return BrokerResult.Ok(new { results }, "openrouter.ai", $"{results.Count} results");
    }
}
