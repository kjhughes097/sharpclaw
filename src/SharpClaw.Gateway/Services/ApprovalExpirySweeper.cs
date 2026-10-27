using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Services;

/// <summary>Expires overdue pending approvals every <c>Approvals:SweepIntervalSeconds</c>.</summary>
public sealed class ApprovalExpirySweeper(
    IServiceScopeFactory scopes, ApprovalDecisions decisions, TimeProvider time,
    IOptions<ApprovalsOptions> options, ILogger<ApprovalExpirySweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.SweepIntervalSeconds));
        using var timer = new PeriodicTimer(interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("approval sweep failed: {Error}", ex.GetType().Name);
            }
        }
    }

    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var expired = await decisions.ExpireDueAsync(db, ct);
        if (expired > 0)
            logger.LogInformation("approvals expired: count={Count}", expired);
        return expired;
    }
}
