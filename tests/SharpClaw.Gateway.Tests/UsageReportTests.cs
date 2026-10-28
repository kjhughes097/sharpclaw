using SharpClaw.Contracts;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Tests;

/// <summary>The usage windows and aggregation rules (3.3.1 spec section 3.1), without a database.</summary>
public class UsageReportTests
{
    private static readonly Guid Ade = Guid.CreateVersion7();
    private static readonly Guid Cody = Guid.CreateVersion7();
    private static readonly (Guid, string)[] Agents = [(Cody, "cody"), (Ade, "ade")];

    private static DateTime Utc(string s) => DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    private static UsageReport.CallRow Call(Guid agent, string at, int prompt = 100, int completion = 10,
        double cost = 0.01, string model = "m/a", bool known = true) =>
        new(agent, model, Utc(at), prompt, completion, cost, known);

    [Fact]
    public void Today_is_hourly_from_utc_midnight_through_the_current_hour()
    {
        var w = UsageReport.WindowFor(UsageRanges.Today, Utc("2026-10-28T13:20:00Z"))!;
        Assert.Equal(Utc("2026-10-28T00:00:00Z"), w.From);
        Assert.Equal(TimeSpan.FromHours(1), w.Bucket);
        Assert.Equal(14, w.BucketCount);
    }

    [Fact]
    public void Today_just_after_midnight_has_one_bucket()
    {
        var w = UsageReport.WindowFor(UsageRanges.Today, Utc("2026-10-28T00:00:05Z"))!;
        Assert.Equal(Utc("2026-10-28T00:00:00Z"), w.From);
        Assert.Equal(1, w.BucketCount);
    }

    [Fact]
    public void Seven_days_is_28_six_hour_buckets_aligned_to_the_quarter_day()
    {
        var w = UsageReport.WindowFor(UsageRanges.Week, Utc("2026-10-28T13:20:00Z"))!;
        // Current block starts 12:00; 27 blocks back is 6 days 18 hours earlier.
        Assert.Equal(Utc("2026-10-21T18:00:00Z"), w.From);
        Assert.Equal(TimeSpan.FromHours(6), w.Bucket);
        Assert.Equal(28, w.BucketCount);
    }

    [Fact]
    public void Thirty_days_is_daily_ending_today()
    {
        var w = UsageReport.WindowFor(UsageRanges.Month, Utc("2026-10-28T13:20:00Z"))!;
        Assert.Equal(Utc("2026-09-29T00:00:00Z"), w.From);
        Assert.Equal(30, w.BucketCount);
    }

    [Fact]
    public void Unknown_range_has_no_window() =>
        Assert.Null(UsageReport.WindowFor("1y", Utc("2026-10-28T13:20:00Z")));

    [Fact]
    public void Buckets_are_zero_filled_ascending_and_utc()
    {
        var now = Utc("2026-10-28T03:30:00Z");
        var report = UsageReport.Build(UsageRanges.Today, UsageReport.WindowFor(UsageRanges.Today, now)!,
            [Call(Ade, "2026-10-28T02:15:00Z")], Agents);

        Assert.Equal(4, report.Buckets.Count);
        Assert.Equal(3600, report.BucketSeconds);
        Assert.All(report.Buckets, b => Assert.Equal(DateTimeKind.Utc, b.Start.Kind));
        Assert.Equal([0, 0, 1, 0], report.Buckets.Select(b => b.Calls));
        Assert.Equal(Utc("2026-10-28T02:00:00Z"), report.Buckets[2].Start);
    }

    [Fact]
    public void Calls_before_the_window_are_ignored()
    {
        var now = Utc("2026-10-28T03:30:00Z");
        var report = UsageReport.Build(UsageRanges.Today, UsageReport.WindowFor(UsageRanges.Today, now)!,
            [Call(Ade, "2026-10-27T23:59:59Z"), Call(Ade, "2026-10-28T00:00:00Z")], Agents);
        Assert.Equal(1, report.Totals.Calls);
        Assert.Equal(1, report.Buckets[0].Calls);
    }

    [Fact]
    public void Unknown_usage_counts_no_tokens_but_keeps_cost()
    {
        var now = Utc("2026-10-28T03:30:00Z");
        var report = UsageReport.Build(UsageRanges.Today, UsageReport.WindowFor(UsageRanges.Today, now)!,
            [Call(Ade, "2026-10-28T01:00:00Z", 500, 50, 0.02, known: false), Call(Ade, "2026-10-28T01:10:00Z")],
            Agents);
        var t = report.Totals;
        Assert.Equal((2, 100L, 10L, 1), (t.Calls, t.PromptTokens, t.CompletionTokens, t.UnknownUsageCalls));
        Assert.Equal(0.03, t.CostUsd, 6);
    }

    [Fact]
    public void By_model_is_sorted_by_cost_and_by_agent_lists_every_agent_by_name()
    {
        var now = Utc("2026-10-28T03:30:00Z");
        var report = UsageReport.Build(UsageRanges.Today, UsageReport.WindowFor(UsageRanges.Today, now)!,
            [
                Call(Ade, "2026-10-28T01:00:00Z", cost: 0.01, model: "cheap"),
                Call(Ade, "2026-10-28T01:00:00Z", cost: 0.05, model: "dear"),
                Call(Ade, "2026-10-28T02:00:00Z", cost: 0.01, model: "cheap"),
            ], Agents);

        Assert.Equal(["dear", "cheap"], report.ByModel.Select(m => m.Model));
        Assert.Equal(2, report.ByModel[1].Calls);
        Assert.Equal(["ade", "cody"], report.ByAgent.Select(a => a.AgentName));
        Assert.Equal(3, report.ByAgent[0].Calls);
        Assert.Equal(0, report.ByAgent[1].Calls);
    }
}
