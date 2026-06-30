using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpClaw.Configuration;
using SharpClaw.Models;

namespace SharpClaw.Loading;

/// <summary>
/// Persists comments on tickets as one JSON file per ticket in the workspace.
/// Ticket IDs are globally unique, so a flat directory keyed by ticket id is safe
/// and survives moves between projects.
/// </summary>
public sealed class TicketCommentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _commentsDir;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<TicketComment>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<TicketCommentStore> _logger;

    public TicketCommentStore(IOptions<SharpClawOptions> options, ILogger<TicketCommentStore> logger)
    {
        _logger = logger;
        var workspace = options.Value.WorkspacePath;
        _commentsDir = string.IsNullOrWhiteSpace(workspace)
            ? Path.Combine(AppContext.BaseDirectory, "ticket-comments")
            : Path.Combine(workspace, "ticket-comments");

        Directory.CreateDirectory(_commentsDir);
        LoadFromDisk();
    }

    public IReadOnlyList<TicketComment> GetForTicket(string ticketId)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(ticketId, out var list))
                return Array.Empty<TicketComment>();
            return list
                .OrderBy(c => c.CreatedUtc)
                .ToList();
        }
    }

    public TicketComment? Get(string ticketId, string commentId)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(ticketId, out var list))
                return null;
            return list.FirstOrDefault(c => string.Equals(c.Id, commentId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public TicketComment Add(string ticketId, string author, string content)
    {
        var comment = new TicketComment
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            TicketId = ticketId,
            Author = string.IsNullOrWhiteSpace(author) ? "user" : author.Trim(),
            Content = content,
        };

        lock (_lock)
        {
            if (!_cache.TryGetValue(ticketId, out var list))
            {
                list = new List<TicketComment>();
                _cache[ticketId] = list;
            }
            list.Add(comment);
            WriteToDisk(ticketId, list);
        }

        return comment;
    }

    public TicketComment? Update(string ticketId, string commentId, string content, string? author)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(ticketId, out var list))
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
            WriteToDisk(ticketId, list);
            return updated;
        }
    }

    public bool Delete(string ticketId, string commentId, string? author)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(ticketId, out var list))
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
                _cache.Remove(ticketId);
                var path = GetFilePath(ticketId);
                if (File.Exists(path))
                    File.Delete(path);
            }
            else
            {
                WriteToDisk(ticketId, list);
            }
            return true;
        }
    }

    public void DeleteAllForTicket(string ticketId)
    {
        lock (_lock)
        {
            if (_cache.Remove(ticketId))
            {
                var path = GetFilePath(ticketId);
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
                var comments = JsonSerializer.Deserialize<List<TicketComment>>(json, JsonOptions);
                if (comments is { Count: > 0 })
                {
                    var ticketId = Path.GetFileNameWithoutExtension(file);
                    _cache[ticketId] = comments;
                    loaded += comments.Count;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse ticket comments file: {File}", file);
            }
        }

        _logger.LogInformation("Loaded {Count} ticket comments from disk", loaded);
    }

    private void WriteToDisk(string ticketId, List<TicketComment> comments)
    {
        var path = GetFilePath(ticketId);
        var json = JsonSerializer.Serialize(comments, JsonOptions);
        File.WriteAllText(path, json);
    }

    private string GetFilePath(string ticketId) => Path.Combine(_commentsDir, $"{ticketId}.json");
}
