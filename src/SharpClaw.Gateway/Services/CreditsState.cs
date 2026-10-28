using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Endpoints;

namespace SharpClaw.Gateway.Services;

/// <summary>
/// The last known OpenRouter credits balance, held in memory only (lost on restart). Written by
/// <see cref="CreditsRefresher"/>, read by <c>GET /api/credits</c>.
/// </summary>
public sealed class CreditsState(
    TimeProvider time, IOptions<ProviderOptions> provider, IOptions<OpenRouterOptions> openRouter,
    IOptions<CreditsOptions> options)
{
    public sealed record Snapshot(
        double? TotalCredits, double? TotalUsage, DateTime? AsOf, DateTime? LastAttemptAt, bool LastAttemptOk);

    private Snapshot _current = new(null, null, null, null, false);

    public Snapshot Current => Volatile.Read(ref _current);

    /// <summary>Whether the refresher should run at all: a real provider and a management key.</summary>
    public bool Enabled => !IsFake && !string.IsNullOrWhiteSpace(openRouter.Value.ManagementKey);

    private bool IsFake =>
        string.Equals(provider.Value.Type, SystemEndpoints.FakeProvider, StringComparison.OrdinalIgnoreCase);

    public void RecordSuccess(double totalCredits, double totalUsage, DateTime at) =>
        Volatile.Write(ref _current, new Snapshot(totalCredits, totalUsage, at, at, true));

    public void RecordFailure(DateTime at) =>
        Volatile.Write(ref _current, Current with { LastAttemptAt = at, LastAttemptOk = false });

    public CreditsResponse ToResponse()
    {
        var s = Current;
        var o = options.Value;
        var status = Status(s, time.GetUtcNow().UtcDateTime, o.RefreshHours);
        double? remaining = s.TotalCredits is { } c && s.TotalUsage is { } u ? c - u : null;
        return new CreditsResponse(status, remaining, s.TotalCredits, s.TotalUsage, s.AsOf, s.LastAttemptAt, o.LowThresholdUsd);
    }

    private string Status(Snapshot s, DateTime now, int refreshHours)
    {
        if (IsFake)
            return CreditsStatuses.Unsupported;
        if (string.IsNullOrWhiteSpace(openRouter.Value.ManagementKey))
            return CreditsStatuses.NotConfigured;
        if (s.LastAttemptAt is not null && !s.LastAttemptOk)
            return CreditsStatuses.Failing;
        // An hour's grace beyond the refresh interval before a successful value counts as stale.
        return s.AsOf is { } asOf && now - asOf <= TimeSpan.FromHours(refreshHours + 1)
            ? CreditsStatuses.Ok
            : CreditsStatuses.Stale;
    }
}
