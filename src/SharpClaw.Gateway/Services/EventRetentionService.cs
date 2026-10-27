using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Services;

/// <summary>Deletes durable events past retention, at startup and daily, and records <c>pruned_through</c>.</summary>
public sealed class EventRetentionService(
    IServiceScopeFactory scopes, TimeProvider time, IOptions<EventsOptions> options,
    ILogger<EventRetentionService> logger) : BackgroundService
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
                logger.LogError("event pruning failed: {Error}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task PruneAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-options.Value.RetentionDays);

        var maxPruned = await db.ThreadEvents.Where(e => e.CreatedAt < cutoff).MaxAsync(e => (long?)e.Id, ct);
        if (maxPruned is null)
            return;

        // Ids are assigned in time order, so everything up to the newest expired id goes.
        var deleted = await db.ThreadEvents.Where(e => e.Id <= maxPruned).ExecuteDeleteAsync(ct);
        await db.EventLogState.Where(s => s.Id == 1 && s.PrunedThrough < maxPruned)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PrunedThrough, maxPruned.Value), ct);
        logger.LogInformation("events pruned: count={Count} pruned_through={PrunedThrough}", deleted, maxPruned);
    }
}
