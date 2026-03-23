using System.Text.Json;

namespace SharpClaw.Core;

/// <summary>
/// Backend-neutral description of a tool's schema, suitable for any LLM backend.
/// </summary>
public sealed record ToolSchema(string Name, string? Description, JsonElement InputSchema);

/// <summary>
/// A tool invocation requested by the model.
/// </summary>
public sealed record ToolCall(string Name, IReadOnlyDictionary<string, object?> Arguments);

/// <summary>
/// The result of executing a tool call.
/// </summary>
public sealed record ToolCallResult(string Content, bool IsError);

/// <summary>
/// Role of a participant in a conversation.
/// </summary>
public enum ChatRole { User, Assistant }

/// <summary>
/// A single turn in a conversation history.
/// </summary>
public sealed record ChatMessage(ChatRole Role, string Content);
