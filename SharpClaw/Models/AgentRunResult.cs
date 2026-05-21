namespace SharpClaw.Models;

public sealed record AgentRunResult
{
    public bool Success { get; private init; }
    public string? Response { get; private init; }
    public string? SessionId { get; private init; }
    public string? Error { get; private init; }
    public int? InputTokens { get; private init; }
    public int? OutputTokens { get; private init; }

    public static AgentRunResult Ok(string response, string? sessionId = null, int? inputTokens = null, int? outputTokens = null) =>
        new() { Success = true, Response = response, SessionId = sessionId, InputTokens = inputTokens, OutputTokens = outputTokens };

    public static AgentRunResult Fail(string error) =>
        new() { Success = false, Error = error };
}
