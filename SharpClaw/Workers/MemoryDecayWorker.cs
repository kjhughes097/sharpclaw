using Microsoft.Extensions.Options;
using SharpClaw.Configuration;
using SharpClaw.Memory;

namespace SharpClaw.Workers;

/// <summary>
/// Runs weekly trust decay on semantic memories and prunes those below threshold.
/// </summary>
public sealed class MemoryDecayWorker(
    SemanticMemoryService memoryService,
    IOptions<SemanticMemoryOptions> options,
    ILogger<MemoryDecayWorker> logger) : BackgroundService
{
    private static readonly TimeSpan DecayInterval = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Memory decay worker disabled (SemanticMemory not enabled)");
            return;
        }

        logger.LogInformation("Memory decay worker started — running every {Days} days", DecayInterval.TotalDays);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(DecayInterval, stoppingToken);
                await RunDecayAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in memory decay tick");
            }
        }

        logger.LogInformation("Memory decay worker stopped");
    }

    private async Task RunDecayAsync(CancellationToken ct)
    {
        var countBefore = await memoryService.GetMemoryCountAsync(ct: ct);
        await memoryService.DecayAsync(ct);
        var countAfter = await memoryService.GetMemoryCountAsync(ct: ct);

        logger.LogInformation(
            "Memory decay complete: {Before} memories before, {After} after ({Pruned} pruned)",
            countBefore, countAfter, countBefore - countAfter);
    }
}
