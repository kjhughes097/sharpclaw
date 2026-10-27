using Microsoft.EntityFrameworkCore;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Auth;

/// <summary>Daily: deletes sessions that expired or were revoked more than 7 days ago.</summary>
public sealed class SessionPruningService(
    IServiceScopeFactory scopes, TimeProvider time, ILogger<SessionPruningService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1), time);
        do
        {
            try
            {
                await PruneAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("session pruning failed: {Error}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> PruneAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-7);
        return await db.Sessions.Where(s => s.ExpiresAt < cutoff || (s.RevokedAt != null && s.RevokedAt < cutoff))
            .ExecuteDeleteAsync(ct);
    }
}
