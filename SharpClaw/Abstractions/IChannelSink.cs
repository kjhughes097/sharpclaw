namespace SharpClaw.Abstractions;

/// <summary>
/// Transport-agnostic interface for delivering messages to a connected channel.
/// </summary>
public interface IChannelSink
{
    /// <summary>
    /// Unique identifier for this channel connection (e.g. WebSocket connection ID or Telegram chat ID).
    /// </summary>
    string ChannelId { get; }

    /// <summary>
    /// The type of transport this sink represents.
    /// </summary>
    ChannelType Type { get; }

    /// <summary>
    /// Delivers a message to this channel.
    /// </summary>
    Task DeliverAsync(string text, CancellationToken ct = default);
}

public enum ChannelType
{
    Web,
    Telegram
}
