using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Telemetry;

/// <summary>
/// All gateway metrics. Meter name is "SharpClaw.Gateway". Labels are limited to agent name,
/// model, status, type and listener — never thread, message, correlation id or token.
/// </summary>
public sealed class GatewayMetrics : IDisposable
{
    public const string MeterName = "SharpClaw.Gateway";

    private readonly Meter _meter;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly IThreadEventBus _bus;

    private readonly Counter<long> _calls;
    private readonly Counter<long> _tokens;
    private readonly Counter<double> _cost;
    private readonly Histogram<double> _duration;
    private readonly Histogram<double> _ttft;
    private readonly Counter<long> _budgetExceeded;
    private readonly Histogram<double> _messageDuration;
    private readonly Counter<long> _authFailures;
    private readonly Counter<long> _toolCalls;
    private readonly Counter<long> _approvals;
    private readonly Histogram<double> _approvalDecisionTime;

    private readonly ConcurrentDictionary<string, double> _lastSeenUnix = new();

    public GatewayMetrics(IServiceScopeFactory scopeFactory, TimeProvider time, IThreadEventBus bus)
    {
        _scopeFactory = scopeFactory;
        _time = time;
        _bus = bus;
        _meter = new Meter(MeterName);

        _calls = _meter.CreateCounter<long>("sharpclaw.llm.calls", "{call}");
        _tokens = _meter.CreateCounter<long>("sharpclaw.llm.tokens", "{token}");
        _cost = _meter.CreateCounter<double>("sharpclaw.llm.cost_usd", "USD");
        // Second-scale buckets: the SDK defaults are designed for milliseconds.
        var llmBuckets = new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 20, 30, 60, 120],
        };
        var messageBuckets = new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [0.1, 0.25, 0.5, 1, 2, 5, 10, 20, 30, 60, 120, 300],
        };
        _duration = _meter.CreateHistogram("sharpclaw.llm.duration", "s", null, null, llmBuckets);
        _ttft = _meter.CreateHistogram("sharpclaw.llm.time_to_first_token", "s", null, null, llmBuckets);
        _budgetExceeded = _meter.CreateCounter<long>("sharpclaw.budget.exceeded", "{rejection}");
        _messageDuration = _meter.CreateHistogram("sharpclaw.message.duration", "s", null, null, messageBuckets);
        _authFailures = _meter.CreateCounter<long>("sharpclaw.auth.failures", "{failure}");
        _toolCalls = _meter.CreateCounter<long>("sharpclaw.tool.calls", "{call}");
        _approvals = _meter.CreateCounter<long>("sharpclaw.approvals", "{approval}");
        _approvalDecisionTime = _meter.CreateHistogram("sharpclaw.approval.decision_time", "s", null, null,
            new InstrumentAdvice<double> { HistogramBucketBoundaries = [1, 5, 10, 30, 60, 120, 300, 600] });

        _meter.CreateObservableGauge("sharpclaw.budget.remaining_usd", ObserveBudgetRemaining, "USD");
        _meter.CreateObservableGauge("sharpclaw.inbox.depth", ObserveInboxDepth, "{message}");
        _meter.CreateObservableGauge("sharpclaw.agent.last_seen", ObserveLastSeen, "s");
        _meter.CreateObservableGauge("sharpclaw.sse.subscribers", ObserveSubscribers, "{subscriber}");
        _meter.CreateObservableGauge("sharpclaw.approvals.pending", ObservePendingApprovals, "{approval}");
    }

    public void RecordCall(string agent, string model, string status) =>
        _calls.Add(1, Tag("agent", agent), Tag("model", model), Tag("status", status));

    public void RecordTokens(string agent, string model, int prompt, int completion)
    {
        if (prompt > 0)
            _tokens.Add(prompt, Tag("agent", agent), Tag("model", model), Tag("type", "prompt"));
        if (completion > 0)
            _tokens.Add(completion, Tag("agent", agent), Tag("model", model), Tag("type", "completion"));
    }

    public void RecordCost(string agent, string model, double costUsd)
    {
        if (costUsd > 0)
            _cost.Add(costUsd, Tag("agent", agent), Tag("model", model));
    }

    public void RecordDuration(string agent, string model, double seconds) =>
        _duration.Record(seconds, Tag("agent", agent), Tag("model", model));

    public void RecordTtft(string agent, string model, double seconds) =>
        _ttft.Record(seconds, Tag("agent", agent), Tag("model", model));

    public void RecordBudgetExceeded(string agent) =>
        _budgetExceeded.Add(1, Tag("agent", agent));

    public void RecordMessageDuration(string agent, double seconds) =>
        _messageDuration.Record(seconds, Tag("agent", agent));

    /// <summary>Tool names come from a fixed set, so they are acceptable labels.</summary>
    public void RecordToolCall(string agent, string tool, string status) =>
        _toolCalls.Add(1, Tag("agent", agent), Tag("tool", tool), Tag("status", status));

    /// <summary>Counted when an approval is decided or expires.</summary>
    public void RecordApproval(string agent, string status) =>
        _approvals.Add(1, Tag("agent", agent), Tag("status", status));

    /// <summary>From creation to decision or expiry.</summary>
    public void RecordApprovalDecisionTime(string agent, string status, double seconds) =>
        _approvalDecisionTime.Record(seconds, Tag("agent", agent), Tag("status", status));

    public void RecordAuthFailure(string listener) =>
        _authFailures.Add(1, Tag("listener", listener));

    public void MarkSeen(string agent) =>
        _lastSeenUnix[agent] = _time.GetUtcNow().ToUnixTimeSeconds();

    private IEnumerable<Measurement<double>> ObserveLastSeen() =>
        _lastSeenUnix.Select(kv => new Measurement<double>(kv.Value, Tag("agent", kv.Key)));

    private IEnumerable<Measurement<int>> ObserveSubscribers()
    {
        var (thread, global) = _bus.SubscriberCounts();
        return new[]
        {
            new Measurement<int>(thread, Tag("scope", "thread")),
            new Measurement<int>(global, Tag("scope", "global")),
        };
    }

    private IEnumerable<Measurement<int>> ObservePendingApprovals()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var counts = db.Approvals.AsNoTracking()
            .Where(a => a.Status == ApprovalStatuses.Pending)
            .GroupBy(a => a.AgentId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        // Every agent reports, so the gauge drops to 0 rather than going stale.
        return db.Agents.AsNoTracking().ToList()
            .Select(a => new Measurement<int>(counts.GetValueOrDefault(a.Id), Tag("agent", a.Name)));
    }

    private IEnumerable<Measurement<double>> ObserveBudgetRemaining()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var dayStart = DayStartUtc();
        var results = new List<Measurement<double>>();
        foreach (var a in db.Agents.AsNoTracking().ToList())
        {
            var spent = db.LlmCalls.Where(c => c.AgentId == a.Id && c.CreatedAt >= dayStart)
                .Sum(c => (double?)c.CostUsd) ?? 0;
            var remaining = Math.Max(0, a.DailyBudgetUsd - spent);
            results.Add(new Measurement<double>(remaining, Tag("agent", a.Name)));
        }
        return results;
    }

    private IEnumerable<Measurement<double>> ObserveInboxDepth()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var rows = db.Messages.AsNoTracking()
            .Where(m => m.Role == "user" && m.Status == "pending")
            .GroupBy(m => m.AgentId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToList();
        var names = db.Agents.AsNoTracking().ToDictionary(a => a.Id, a => a.Name);
        return rows.Where(r => names.ContainsKey(r.Key))
            .Select(r => new Measurement<double>(r.Count, Tag("agent", names[r.Key])));
    }

    private DateTime DayStartUtc()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);
    }

    private static KeyValuePair<string, object?> Tag(string k, object? v) => new(k, v);

    public void Dispose() => _meter.Dispose();
}
