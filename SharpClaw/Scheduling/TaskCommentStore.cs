using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpClaw.Configuration;
using SharpClaw.Models;

namespace SharpClaw.Scheduling;

/// <summary>
/// Persists comments on scheduled tasks as one JSON file per task in the workspace.
/// </summary>
public sealed class TaskCommentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _commentsDir;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<TaskComment>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<TaskCommentStore> _logger;

    public TaskCommentStore(IOptions<SharpClawOptions> options, ILogger<TaskCommentStore> logger)
    {
        _logger = logger;
        var workspace = options.Value.WorkspacePath;
        _commentsDir = string.IsNullOrWhiteSpace(workspace)
            ? Path.Combine(AppContext.BaseDirectory, "task-comments")
            : Path.Combine(workspace, "task-comments");

        Directory.CreateDirectory(_commentsDir);
        LoadFromDisk();
    }

    public IReadOnlyList<TaskComment> GetForTask(string taskId)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(taskId, out var list))
                return Array.Empty<TaskComment>();
            return list
                .OrderBy(c => c.CreatedUtc)
                .ToList();
        }
    }

    public TaskComment? Get(string taskId, string commentId)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(taskId, out var list))
                return null;
            return list.FirstOrDefault(c => string.Equals(c.Id, commentId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public TaskComment Add(string taskId, string author, string content)
    {
        var comment = new TaskComment
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            TaskId = taskId,
            Author = string.IsNullOrWhiteSpace(author) ? "user" : author.Trim(),
            Content = content,
        };

        lock (_lock)
        {
            if (!_cache.TryGetValue(taskId, out var list))
            {
                list = new List<TaskComment>();
                _cache[taskId] = list;
            }
            list.Add(comment);
            WriteToDisk(taskId, list);
        }

        return comment;
    }

    public TaskComment? Update(string taskId, string commentId, string content, string? author)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(taskId, out var list))
                return null;

            var index = list.FindIndex(c => string.Equals(c.Id, commentId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return null;

            var existing = list[index];
            if (!string.IsNullOrWhiteSpace(author) &&
                !string.Equals(existing.Author, author.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var updated = existing with
            {
                Content = content,
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
            list[index] = updated;
            WriteToDisk(taskId, list);
            return updated;
        }
    }

    public bool Delete(string taskId, string commentId, string? author)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(taskId, out var list))
                return false;

            var index = list.FindIndex(c => string.Equals(c.Id, commentId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return false;

            if (!string.IsNullOrWhiteSpace(author) &&
                !string.Equals(list[index].Author, author.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            list.RemoveAt(index);
            if (list.Count == 0)
            {
                _cache.Remove(taskId);
                var path = GetFilePath(taskId);
                if (File.Exists(path))
                    File.Delete(path);
            }
            else
            {
                WriteToDisk(taskId, list);
            }
            return true;
        }
    }

    public void DeleteAllForTask(string taskId)
    {
        lock (_lock)
        {
            if (_cache.Remove(taskId))
            {
                var path = GetFilePath(taskId);
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
    }

    private void LoadFromDisk()
    {
        if (!Directory.Exists(_commentsDir))
            return;

        var loaded = 0;
        foreach (var file in Directory.EnumerateFiles(_commentsDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var comments = JsonSerializer.Deserialize<List<TaskComment>>(json, JsonOptions);
                if (comments is { Count: > 0 })
                {
                    var taskId = Path.GetFileNameWithoutExtension(file);
                    _cache[taskId] = comments;
                    loaded += comments.Count;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse task comments file: {File}", file);
            }
        }

        _logger.LogInformation("Loaded {Count} task comments from disk", loaded);
    }

    private void WriteToDisk(string taskId, List<TaskComment> comments)
    {
        var path = GetFilePath(taskId);
        var json = JsonSerializer.Serialize(comments, JsonOptions);
        File.WriteAllText(path, json);
    }

    private string GetFilePath(string taskId) => Path.Combine(_commentsDir, $"{taskId}.json");
}
