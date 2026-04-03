using System.Collections.Concurrent;
using System.Text.Json;

namespace SharpClaw.Telegram;

public sealed class SessionMappingStore
{
    private readonly ConcurrentDictionary<long, string> _chatSessions = new();
    private readonly string _persistencePath;
    private readonly ILogger<SessionMappingStore> _logger;
    private readonly object _saveLock = new();

    public SessionMappingStore(IConfiguration configuration, ILogger<SessionMappingStore> logger)
    {
        _logger = logger;
        _persistencePath = configuration["Telegram:MappingStorePath"]
            ?? DefaultMappingStorePath();

        Load();
    }

    public bool TryGetSession(long chatId, out string sessionId)
        => _chatSessions.TryGetValue(chatId, out sessionId!);

    public void SetSession(long chatId, string sessionId)
    {
        _chatSessions[chatId] = sessionId;
        Save();
    }

    public void RemoveSession(long chatId)
    {
        _chatSessions.TryRemove(chatId, out _);
        Save();
    }

    private static string DefaultMappingStorePath()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Path.Combine(baseDir, "sharpclaw", "telegram-session-mappings.json");
    }

    private void Load()
    {
        if (!File.Exists(_persistencePath))
            return;

        try
        {
            var json = File.ReadAllText(_persistencePath);
            var dict = JsonSerializer.Deserialize<Dictionary<long, string>>(json);
            if (dict is not null)
            {
                foreach (var (k, v) in dict)
                    _chatSessions[k] = v;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load session mappings from '{Path}'", _persistencePath);
        }
    }

    private void Save()
    {
        var snapshot = new Dictionary<long, string>(_chatSessions);

        lock (_saveLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_persistencePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(snapshot);
                File.WriteAllText(_persistencePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save session mappings to '{Path}'", _persistencePath);
            }
        }
    }
}
