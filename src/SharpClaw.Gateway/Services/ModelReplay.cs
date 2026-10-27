using System.Text.Json;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Services;

/// <summary>
/// Maps stored thread rows to model-facing messages (pre-3.3 spec section 5.4). Untrusted tool
/// results from earlier turns are replaced by a stub, so content that may carry prompt injection
/// never persists in a thread's model context.
/// </summary>
public static class ModelReplay
{
    public const string UntrustedStub =
        """{"ok":true,"omitted":"Untrusted content from an earlier turn is not replayed. Call the tool again if you need it."}""";

    /// <param name="currentCorrelationId">The turn being processed; its untrusted results are kept.</param>
    public static ContextMessage ToContextMessage(Message row, Guid currentCorrelationId)
    {
        switch (row.Kind)
        {
            case MessageKinds.ToolRequest:
                var calls = JsonSerializer.Deserialize<List<ChatToolCall>>(row.ToolCalls ?? "[]", SharpClawJson.Options)!;
                return new ContextMessage(Roles.Assistant, string.IsNullOrEmpty(row.Content) ? null : row.Content,
                    calls.Select(c => new ContextToolCall(c.Id, "function", new ContextFunction(c.Name, c.Arguments))).ToList());
            case MessageKinds.ToolResult:
                var stub = row.Untrusted && row.CorrelationId != currentCorrelationId;
                return new ContextMessage(Roles.Tool, stub ? UntrustedStub : row.Content, ToolCallId: row.ToolCallId);
            default:
                return new ContextMessage(row.Role, row.Content);
        }
    }
}
