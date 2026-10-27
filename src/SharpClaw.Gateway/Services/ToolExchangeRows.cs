using System.Text.Json;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Services;

/// <summary>
/// Validates the tool exchanges in an agent reply and turns them into thread rows
/// (pre-3.3 spec section 5.3), re-applying the gateway's truncation limits.
/// </summary>
public static class ToolExchangeRows
{
    /// <summary>Returns a validation message, or null when the exchanges are acceptable.</summary>
    public static string? Validate(IReadOnlyList<ToolExchangeDto> exchanges, ToolsOptions options)
    {
        if (exchanges.Count > options.MaxExchangesPerReply)
            return $"at most {options.MaxExchangesPerReply} exchanges are allowed.";

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ex in exchanges)
        {
            if (ex.ToolCalls is not { Count: > 0 } || ex.Results is null)
                return "each exchange needs at least one tool call and its results.";
            foreach (var call in ex.ToolCalls)
            {
                if (string.IsNullOrEmpty(call.Id) || string.IsNullOrEmpty(call.Name) || call.Arguments is null)
                    return "each tool call needs id, name and arguments.";
                if (!seen.Add(call.Id))
                    return "tool call ids must be unique within a reply.";
            }

            var callIds = ex.ToolCalls.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            var resultIds = ex.Results.Select(r => r.ToolCallId).ToList();
            if (resultIds.Count != callIds.Count || resultIds.Distinct(StringComparer.Ordinal).Count() != resultIds.Count ||
                !resultIds.All(callIds.Contains))
                return "every tool call needs exactly one result, and every result one call.";
            if (ex.Results.Any(r => r.Content is null))
                return "each tool result needs content.";
        }
        return null;
    }

    /// <summary>
    /// The rows for the exchanges, in order: a <c>tool_request</c>, then one <c>tool_result</c> per
    /// call in call order. <c>Seq</c> is assigned from <paramref name="nextSeq"/>.
    /// </summary>
    public static List<Message> Build(
        IReadOnlyList<ToolExchangeDto> exchanges, Message userMsg, DateTime now, ToolsOptions options, ref int nextSeq)
    {
        var rows = new List<Message>();
        foreach (var ex in exchanges)
        {
            var truncated = false;
            var calls = ex.ToolCalls.Select(c =>
            {
                var args = ToolHistoryLimits.TruncateArguments(c.Arguments, options.MaxStoredArgumentStringChars, out var cut);
                truncated |= cut;
                return c with { Arguments = args };
            }).ToList();
            var request = Row(userMsg, now, nextSeq++, Roles.Assistant, MessageKinds.ToolRequest, ex.AssistantContent ?? "");
            request.ToolCalls = JsonSerializer.Serialize(calls, SharpClawJson.Options);
            request.Truncated = truncated;
            rows.Add(request);

            var results = ex.Results.ToDictionary(r => r.ToolCallId, StringComparer.Ordinal);
            foreach (var call in ex.ToolCalls)
            {
                var result = results[call.Id];
                var row = Row(userMsg, now, nextSeq++, Roles.Tool, MessageKinds.ToolResult,
                    ToolHistoryLimits.TruncateText(result.Content, options.MaxStoredResultChars, out var cut));
                row.ToolCallId = call.Id;
                row.ToolOk = result.Ok;
                row.Untrusted = result.Untrusted;
                row.Truncated = cut;
                rows.Add(row);
            }
        }
        return rows;
    }

    private static Message Row(Message userMsg, DateTime now, int seq, string role, string kind, string content) => new()
    {
        Id = Guid.CreateVersion7(),
        ThreadId = userMsg.ThreadId,
        AgentId = userMsg.AgentId,
        Role = role,
        Kind = kind,
        Content = content,
        Status = MessageStatus.Done,
        CorrelationId = userMsg.Id,
        CreatedAt = now,
        CompletedAt = now,
        Seq = seq,
    };
}
