using Microsoft.Extensions.AI;

namespace SharpClaw.Models;

public sealed record LlmSessionRequest(
    string? Model = null,
    string? SystemPrompt = null,
    IReadOnlyList<AIFunction>? Tools = null,
    IReadOnlyDictionary<string, McpServerDefinition>? McpServers = null,
    string? ResumeSessionId = null
);
