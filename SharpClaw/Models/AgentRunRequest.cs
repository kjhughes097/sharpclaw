namespace SharpClaw.Models;

public sealed record AgentRunRequest(
    string Prompt,
    string? Llm = null,
    string? Model = null,
    string? SystemPromptOverride = null,
    string? ResumeSessionId = null,
    IReadOnlyList<string>? ToolNames = null,
    IReadOnlyList<string>? McpServerNames = null,
    IReadOnlyList<string>? LazyMcpNames = null
);
