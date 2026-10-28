using SharpClaw.Contracts;

namespace SharpClaw.Gateway.Services;

/// <summary>
/// Builds the usage report (3.3.1 spec section 3.1): the UTC window and bucket size for a range,
/// then a zero-filled aggregation of the calls in it. Pure, so the bucketing rules are unit-tested
/// without a database; the endpoint only loads rows.
/// </summary>
public static class UsageReport
{
    /// <summary>The columns the report needs from one <c>llm_calls</c> row.</summary>
    public sealed record CallRow(
        Guid AgentId, string Model, DateTime CreatedAt, int PromptTokens, int CompletionTokens, double CostUsd,
        bool UsageKnown);

    public sealed record Window(DateTime From, DateTime To, TimeSpan Bucket, int BucketCount);

    /// <summary>Null for an unknown range.</summary>
    public static Window? WindowFor(string range, DateTime nowUtc)
    {
        var today = nowUtc.Date;
        switch (range)
        {
            case UsageRanges.Today:
                // 00:00 up to and including the current hour; the budget day is UTC too.
                return new Window(Utc(today), nowUtc, TimeSpan.FromHours(1), nowUtc.Hour + 1);
            case UsageRanges.Week:
                var sixHours = TimeSpan.FromHours(6);
                var currentBlock = today.AddHours(nowUtc.Hour / 6 * 6);
                return new Window(Utc(currentBlock - 27 * sixHours), nowUtc, sixHours, 28);
            case UsageRanges.Month:
                return new Window(Utc(today.AddDays(-29)), nowUtc, TimeSpan.FromDays(1), 30);
            default:
                return null;
        }
    }

    public static UsageResponse Build(
        string range, Window window, IReadOnlyList<CallRow> calls, IReadOnlyList<(Guid Id, string Name)> agents)
    {
        var buckets = new Acc[window.BucketCount];
        for (var i = 0; i < buckets.Length; i++)
            buckets[i] = new Acc();
        var byModel = new Dictionary<string, Acc>(StringComparer.Ordinal);
        var byAgent = agents.ToDictionary(a => a.Id, _ => new Acc());
        var total = new Acc();
        var unknown = 0;

        foreach (var c in calls)
        {
            if (c.CreatedAt < window.From)
                continue;
            var index = (int)((c.CreatedAt - window.From).Ticks / window.Bucket.Ticks);
            if (index >= buckets.Length)
                continue;

            buckets[index].Add(c);
            total.Add(c);
            if (!byModel.TryGetValue(c.Model, out var model))
                byModel[c.Model] = model = new Acc();
            model.Add(c);
            if (byAgent.TryGetValue(c.AgentId, out var agent))
                agent.Add(c);
            if (!c.UsageKnown)
                unknown++;
        }

        return new UsageResponse(
            range, window.From, window.To, (int)window.Bucket.TotalSeconds,
            buckets.Select((b, i) => new UsageBucketDto(
                Utc(window.From + i * window.Bucket), b.Calls, b.Prompt, b.Completion, b.Cost)).ToList(),
            byModel.OrderByDescending(m => m.Value.Cost).ThenBy(m => m.Key, StringComparer.Ordinal)
                .Select(m => new UsageByModelDto(m.Key, m.Value.Calls, m.Value.Prompt, m.Value.Completion, m.Value.Cost))
                .ToList(),
            agents.OrderBy(a => a.Name, StringComparer.Ordinal)
                .Select(a => (Agent: a, Acc: byAgent[a.Id]))
                .Select(x => new UsageByAgentDto(
                    x.Agent.Id, x.Agent.Name, x.Acc.Calls, x.Acc.Prompt, x.Acc.Completion, x.Acc.Cost))
                .ToList(),
            new UsageTotalsDto(total.Calls, total.Prompt, total.Completion, total.Cost, unknown));
    }

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);

    private sealed class Acc
    {
        public int Calls;
        public long Prompt;
        public long Completion;
        public double Cost;

        public void Add(CallRow c)
        {
            Calls++;
            // Unknown usage: the provider gave no counts, so count no tokens but keep any recorded cost.
            if (c.UsageKnown)
            {
                Prompt += c.PromptTokens;
                Completion += c.CompletionTokens;
            }
            Cost += c.CostUsd;
        }
    }
}
