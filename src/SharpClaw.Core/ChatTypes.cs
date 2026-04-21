namespace SharpClaw.Core;

/// <summary>Chat message role.</summary>
public enum ChatRole { User, Assistant }

/// <summary>A single message in a conversation.</summary>
public sealed record ChatMessage(ChatRole Role, string Content, string? AgentSlug = null);

/// <summary>Schema advertised to the LLM for a callable tool.</summary>
public sealed record ToolSchema(string Name, string Description, string InputSchemaJson);

/// <summary>A tool invocation requested by the LLM.</summary>
public sealed record ToolCall(string Name, string ArgumentsJson);

/// <summary>The result of executing a tool call.</summary>
public sealed record ToolCallResult(string Content, bool IsError = false);
