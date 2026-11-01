using System.Text;
using System.Text.Json;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Services;

/// <summary>An attachment as replay sees it: <c>Text</c> is the decoded file for text attachments, null for images.</summary>
public sealed record ReplayAttachment(Guid Id, string Kind, string FileName, string? Text);

/// <summary>
/// Maps stored thread rows to model-facing messages (pre-3.3 spec section 5.4). Untrusted tool
/// results from earlier turns are replaced by a stub, so content that may carry prompt injection
/// never persists in a thread's model context.
/// </summary>
public static class ModelReplay
{
    public const string UntrustedStub =
        """{"ok":true,"omitted":"Untrusted content from an earlier turn is not replayed. Call the tool again if you need it."}""";

    /// <summary>What the recipient's model sees before a question another agent sent it (3.5 spec section 5.6).</summary>
    public static string FromAgentPrefix(string sender) => $"[Message from agent {sender}]\n\n";

    /// <param name="currentCorrelationId">The turn being processed; its untrusted results are kept.</param>
    /// <param name="senderNames">Names of the agents that sent user messages in these rows, by id.</param>
    /// <param name="attachments">The row's attachments, when it is a user message that has any.</param>
    public static ContextMessage ToContextMessage(
        Message row, Guid currentCorrelationId, IReadOnlyDictionary<Guid, string>? senderNames = null,
        IReadOnlyList<ReplayAttachment>? attachments = null)
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
                if (row.Role == Roles.User && row.SenderAgentId is { } sender)
                    return new ContextMessage(row.Role,
                        FromAgentPrefix(senderNames?.GetValueOrDefault(sender) ?? "unknown") + row.Content);
                if (row.Role == Roles.User && attachments is { Count: > 0 })
                    return WithAttachments(row.Content, attachments);
                return new ContextMessage(row.Role, row.Content);
        }
    }

    /// <summary>
    /// A user message with its attachments (attachments spec section 4): each text file is inlined
    /// after the text in a fence the file can't close early; each image gets a marker line, and its
    /// id rides along for the gateway to resolve when it calls the provider.
    /// </summary>
    public static ContextMessage WithAttachments(string content, IReadOnlyList<ReplayAttachment> attachments)
    {
        var sb = new StringBuilder(content);
        var imageIds = new List<Guid>();
        foreach (var a in attachments)
        {
            if (sb.Length > 0)
                sb.Append("\n\n");
            if (a.Kind == AttachmentKinds.Image)
            {
                sb.Append("[Attached image: ").Append(a.FileName).Append(']');
                imageIds.Add(a.Id);
                continue;
            }
            var text = a.Text ?? "";
            var fence = new string('`', Math.Max(3, LongestBacktickRun(text) + 1));
            sb.Append("[Attached file: ").Append(a.FileName).Append("]\n")
                .Append(fence).Append('\n').Append(text);
            if (!text.EndsWith('\n'))
                sb.Append('\n');
            sb.Append(fence);
        }
        return new ContextMessage(Roles.User, sb.ToString(), ImageIds: imageIds.Count > 0 ? imageIds : null);
    }

    private static int LongestBacktickRun(string text)
    {
        int longest = 0, run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        return longest;
    }
}
