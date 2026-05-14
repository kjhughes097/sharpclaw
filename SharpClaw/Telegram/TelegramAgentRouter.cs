using System.Collections.Concurrent;

namespace SharpClaw.Telegram;

/// <summary>
/// Maps Telegram chat ID → agent ID.
/// </summary>
public sealed class TelegramAgentRouter
{
    private readonly ConcurrentDictionary<long, string> _routes = new();

    public string? Resolve(long chatId) =>
        _routes.TryGetValue(chatId, out var name) ? name : null;

    public void Map(long chatId, string agentName) =>
        _routes[chatId] = agentName;
}
