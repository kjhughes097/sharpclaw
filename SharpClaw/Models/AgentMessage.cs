namespace SharpClaw.Models;

public sealed record AgentMessage(
    string SessionId,
    string MessageId,
    MessageOrigin Origin,
    string AgentId,
    string Text,
    DateTimeOffset Timestamp
);

public enum MessageOrigin { Web, Telegram, Agent }
