using System.Globalization;
using Cronos;
using Microsoft.Extensions.Options;
using SharpClaw.Configuration;
using SharpClaw.Models;

namespace SharpClaw.Scheduling;

/// <summary>
/// Persists scheduled tasks as individual .task.md files with YAML frontmatter in the workspace.
/// </summary>
public sealed class ScheduleStore
{
    private readonly string _schedulesDir;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ScheduledTask> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ScheduleStore> _logger;

    public ScheduleStore(IOptions<SharpClawOptions> options, ILogger<ScheduleStore> logger)
    {
        _logger = logger;
        var workspace = options.Value.WorkspacePath;
        _schedulesDir = string.IsNullOrWhiteSpace(workspace)
            ? Path.Combine(AppContext.BaseDirectory, "schedules")
            : Path.Combine(workspace, "schedules");

        Directory.CreateDirectory(_schedulesDir);
        LoadFromDisk();
    }

    public IReadOnlyList<ScheduledTask> GetAll()
    {
        lock (_lock)
            return _cache.Values.ToList();
    }

    public ScheduledTask? Get(string id)
    {
        lock (_lock)
            return _cache.GetValueOrDefault(id);
    }

    public IReadOnlyList<ScheduledTask> GetDue(DateTimeOffset now)
    {
        lock (_lock)
            return _cache.Values
                .Where(t => t.Enabled && t.NextRunUtc <= now)
                .ToList();
    }

    public void Save(ScheduledTask task)
    {
        lock (_lock)
        {
            _cache[task.Id] = task;
            WriteToDisk(task);
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            if (!_cache.Remove(id))
                return false;

            var path = GetFilePath(id);
            if (File.Exists(path))
                File.Delete(path);

            return true;
        }
    }

    public static DateTimeOffset? ComputeNextRun(string cronExpression, DateTimeOffset from)
    {
        var cron = CronExpression.Parse(cronExpression, CronFormat.Standard);
        var next = cron.GetNextOccurrence(from.UtcDateTime, inclusive: false);
        return next.HasValue ? new DateTimeOffset(next.Value, TimeSpan.Zero) : null;
    }

    private void LoadFromDisk()
    {
        if (!Directory.Exists(_schedulesDir))
            return;

        foreach (var file in Directory.EnumerateFiles(_schedulesDir, "*.task.md"))
        {
            try
            {
                var task = ParseFile(file);
                if (task is not null)
                    _cache[task.Id] = task;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse schedule file: {File}", file);
            }
        }

        _logger.LogInformation("Loaded {Count} scheduled tasks from disk", _cache.Count);
    }

    private void WriteToDisk(ScheduledTask task)
    {
        var path = GetFilePath(task.Id);
        var content = SerializeTask(task);
        File.WriteAllText(path, content);
    }

    private string GetFilePath(string id) => Path.Combine(_schedulesDir, $"{id}.task.md");

    private static string SerializeTask(ScheduledTask task)
    {
        var lines = new List<string>
        {
            "---",
            $"id: {task.Id}",
            $"agent: {task.AgentId}",
            $"cron: \"{task.CronExpression}\"",
            $"one_off: {task.IsOneOff.ToString().ToLowerInvariant()}",
            $"channel_key: \"{task.ChannelKey}\"",
            $"channel_type: {task.ChannelType}",
            $"created: {task.CreatedUtc:O}",
            $"next_run: {task.NextRunUtc:O}",
        };

        if (task.LastRunUtc.HasValue)
            lines.Add($"last_run: {task.LastRunUtc.Value:O}");

        lines.Add($"enabled: {task.Enabled.ToString().ToLowerInvariant()}");

        if (!string.IsNullOrEmpty(task.Description))
            lines.Add($"description: \"{EscapeYaml(task.Description)}\"");

        lines.Add("---");
        lines.Add(string.Empty);
        lines.Add(task.Prompt);

        return string.Join('\n', lines);
    }

    private static ScheduledTask? ParseFile(string path)
    {
        var content = File.ReadAllText(path);
        if (!content.StartsWith("---"))
            return null;

        var endIndex = content.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (endIndex < 0)
            return null;

        var frontmatter = content[4..endIndex];
        var body = content[(endIndex + 5)..].Trim();

        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in frontmatter.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0) continue;
            var key = line[..colonIdx].Trim();
            var value = line[(colonIdx + 1)..].Trim().Trim('"');
            props[key] = value;
        }

        if (!props.TryGetValue("id", out var id) ||
            !props.TryGetValue("agent", out var agent) ||
            !props.TryGetValue("cron", out var cron))
            return null;

        return new ScheduledTask
        {
            Id = id,
            AgentId = agent,
            Prompt = body,
            CronExpression = cron,
            Description = props.GetValueOrDefault("description") ?? string.Empty,
            IsOneOff = props.TryGetValue("one_off", out var oneOff) &&
                       bool.TryParse(oneOff, out var isOneOff) && isOneOff,
            ChannelKey = props.GetValueOrDefault("channel_key") ?? string.Empty,
            ChannelType = props.TryGetValue("channel_type", out var ct) &&
                          Enum.TryParse<ScheduleChannelType>(ct, true, out var channelType)
                ? channelType
                : ScheduleChannelType.Telegram,
            CreatedUtc = props.TryGetValue("created", out var created) &&
                         DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdDto)
                ? createdDto
                : DateTimeOffset.UtcNow,
            NextRunUtc = props.TryGetValue("next_run", out var nextRun) &&
                         DateTimeOffset.TryParse(nextRun, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var nextRunDto)
                ? nextRunDto
                : DateTimeOffset.UtcNow,
            LastRunUtc = props.TryGetValue("last_run", out var lastRun) &&
                         DateTimeOffset.TryParse(lastRun, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastRunDto)
                ? lastRunDto
                : null,
            Enabled = !props.TryGetValue("enabled", out var enabled) ||
                      !bool.TryParse(enabled, out var isEnabled) || isEnabled,
        };
    }

    private static string EscapeYaml(string value) => value.Replace("\"", "\\\"");
}
