using SharpClaw.Agent.Tools;
using SharpClaw.Contracts;

namespace SharpClaw.Agent;

public enum InboxStatus { Message, Empty, Unauthorized, Disabled, Transient }

public sealed record InboxResult(InboxStatus Status, InboxMessageDto? Message)
{
    public static readonly InboxResult Empty = new(InboxStatus.Empty, null);
    public static readonly InboxResult Unauthorized = new(InboxStatus.Unauthorized, null);
    public static readonly InboxResult Disabled = new(InboxStatus.Disabled, null);
    public static readonly InboxResult Transient = new(InboxStatus.Transient, null);
    public static InboxResult Of(InboxMessageDto m) => new(InboxStatus.Message, m);
}

/// <summary>
/// Outcome of streaming a completion: the accumulated content and any tool calls the model
/// requested, or a short fail code.
/// </summary>
public sealed record ChatOutcome(bool Success, string? Content, string? FailCode)
{
    public IReadOnlyList<ChatToolCall> ToolCalls { get; init; } = Array.Empty<ChatToolCall>();

    public static ChatOutcome Ok(string content, IReadOnlyList<ChatToolCall>? toolCalls = null) =>
        new(true, content, null) { ToolCalls = toolCalls ?? Array.Empty<ChatToolCall>() };

    public static ChatOutcome Fail(string code) => new(false, null, code);
}

/// <summary>The model-ready history for a turn, or a fail code (e.g. <c>context_too_large</c>).</summary>
public sealed record ContextResult(IReadOnlyList<ChatMessage> Messages, string? FailCode = null)
{
    public static ContextResult Fail(string code) => new(Array.Empty<ChatMessage>(), code);
}

/// <summary>Everything the agent loop needs from the gateway's agent listener.</summary>
public interface IGatewayClient
{
    Task<InboxResult> PollInboxAsync(int waitSeconds, CancellationToken ct);

    /// <summary>
    /// The thread's history fitted to the agent's context window by the gateway.
    /// <paramref name="reserveTokens"/> is the caller's estimate for the soul and tool schemas.
    /// </summary>
    Task<ContextResult> GetContextAsync(Guid threadId, int reserveTokens, CancellationToken ct);
    /// <summary>Streams a completion, offering <paramref name="tools"/> to the model (none if empty).</summary>
    Task<ChatOutcome> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages, IReadOnlyCollection<IAgentTool> tools,
        Guid correlationId, Guid threadId, CancellationToken ct);
    /// <summary>Posts the final reply with the turn's tool exchanges (already truncated).</summary>
    Task PostReplyAsync(Guid threadId, string content, Guid correlationId, IReadOnlyList<ToolExchangeDto> exchanges, CancellationToken ct);
    Task FailAsync(Guid messageId, string reason, CancellationToken ct);

    // ----- Tools (3.2) -----

    /// <summary>Asks the gateway to put an approval in front of the human.</summary>
    Task<ApprovalDto> RequestApprovalAsync(CreateApprovalRequest request, CancellationToken ct);

    /// <summary>Long-polls an approval until it is decided or <paramref name="waitSeconds"/> elapse.</summary>
    Task<ApprovalDto> PollApprovalAsync(Guid approvalId, int waitSeconds, CancellationToken ct);

    /// <summary>Reports what happened to a tool call the model requested (for the audit).</summary>
    Task ReportToolCallAsync(ToolCallReportRequest report, CancellationToken ct);

    /// <summary>
    /// Runs a broker tool in the gateway or the agent's sandbox (<c>POST /agent/tools/{tool}</c>).
    /// Returns the tool result for the model; a gateway refusal becomes an error result.
    /// </summary>
    Task<ToolResult> InvokeToolAsync(string tool, InvokeToolRequest request, CancellationToken ct);

    /// <summary>The operator's broker settings for this agent (<c>GET /agent/tools</c>).</summary>
    Task<AgentToolPolicyResponse> GetToolPolicyAsync(CancellationToken ct);

    /// <summary>
    /// Reports the skills loaded at startup (<c>PUT /agent/skills</c>), replacing the previous set.
    /// Throws <see cref="HttpRequestException"/> (with the status code) when the gateway refuses it.
    /// </summary>
    Task ReportSkillsAsync(IReadOnlyList<AgentSkillDto> skills, CancellationToken ct);

    /// <summary>
    /// Smart mode: asks the LLM to classify a call's risk (purpose <c>risk_assessment</c>, never
    /// shown to the user). Returns the reply text, or null if the call failed.
    /// </summary>
    Task<string?> ClassifyRiskAsync(
        IReadOnlyList<ChatMessage> messages, Guid correlationId, Guid threadId, CancellationToken ct);
}

/// <summary>
/// The gateway refused to put a call in front of the human (e.g. a PR touching a blocked path):
/// a hardline refusal with a code and message the model may see.
/// </summary>
public sealed class ApprovalRefusedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface ISoulProvider
{
    /// <summary>Reads the soul (system prompt) fresh each turn.</summary>
    string Read();
}
