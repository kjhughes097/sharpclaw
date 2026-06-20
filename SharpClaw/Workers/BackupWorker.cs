using Cronos;
using Microsoft.Extensions.Options;
using SharpClaw.Backup;
using SharpClaw.Configuration;

namespace SharpClaw.Workers;

public sealed class BackupWorker(
    BackupService backupService,
    IOptions<BackupOptions> options,
    ILogger<BackupWorker> logger) : BackgroundService
{
    private readonly BackupService _backup = backupService;
    private readonly BackupOptions _options = options.Value;
    private readonly ILogger<BackupWorker> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("BackupWorker disabled (Backup:Enabled=false)");
            return;
        }

        CronExpression cron;
        try
        {
            cron = CronExpression.Parse(_options.CronExpression, CronFormat.Standard);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BackupWorker: invalid cron '{Cron}', worker disabled", _options.CronExpression);
            return;
        }

        _logger.LogInformation("BackupWorker started: cron={Cron} root={Root} retentionDays={Days}",
            _options.CronExpression, _options.RootPath, _options.RetentionDays);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var next = cron.GetNextOccurrence(now, inclusive: false);
            if (next is null)
            {
                _logger.LogWarning("BackupWorker: no next occurrence for cron, exiting");
                return;
            }

            var delay = next.Value - now;
            _logger.LogInformation("BackupWorker: next run at {Next:O} (in {Delay:c})", next.Value, delay);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (TaskCanceledException) { return; }

            try
            {
                await _backup.RunBackupAsync(dryRun: false, stoppingToken);
                await _backup.RunRetentionAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BackupWorker: scheduled run failed");
            }
        }
    }
}
