namespace SharpClaw.Core;

/// <summary>
/// Base class for events emitted during a streaming LLM response.
/// </summary>
public abstract record AgentEvent(string Type);

/// <summary>Streamed text token from the LLM.</summary>
public sealed record TokenEvent(string Text) : AgentEvent("token");

/// <summary>The LLM is invoking a tool.</summary>
public sealed record ToolCallEvent(string Tool, string? Input) : AgentEvent("tool_call");

/// <summary>Result of a tool invocation.</summary>
public sealed record ToolResultEvent(string Tool, string Result, bool IsError) : AgentEvent("tool_result");

/// <summary>Status message (e.g. "Routing to Cody...").</summary>
public sealed record StatusEvent(string Message) : AgentEvent("status");

/// <summary>Token usage report for the turn.</summary>
public sealed record UsageEvent(string Provider, int InputTokens, int OutputTokens) : AgentEvent("usage");

/// <summary>Signals the end of a response stream.</summary>
public sealed record DoneEvent(string? Content = null) : AgentEvent("done");
