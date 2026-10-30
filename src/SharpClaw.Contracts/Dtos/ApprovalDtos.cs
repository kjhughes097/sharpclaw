namespace SharpClaw.Contracts;

// ----- Approvals (3.2) -----

/// <summary>
/// The agent asks the human to allow a tool call. <c>Summary</c> never contains content.
/// <c>ArgsHash</c> (<see cref="ToolArgsHash"/>) binds the approval to exactly these arguments; the
/// broker only accepts an approval whose hash matches the call it is asked to run.
/// </summary>
public sealed record CreateApprovalRequest(
    Guid CorrelationId,
    Guid ThreadId,
    string ToolCallId,
    string ToolName,
    string Summary,
    string Risk,
    string Reason,
    string? ArgsHash = null);

public sealed record ApprovalDto(Guid Id, string Status, string? DecidedBy, DateTime ExpiresAt);

public sealed record ApprovalResponse(ApprovalDto Approval);

/// <summary><c>Decision</c> is <c>approved</c> or <c>denied</c>.</summary>
public sealed record DecideApprovalRequest(string Decision);


/// <summary>An approval as shown in the client queue. Never carries tool arguments.</summary>
public sealed record ApprovalDetailDto(
    Guid Id,
    string Agent,
    Guid ThreadId,
    Guid CorrelationId,
    string ToolName,
    string Summary,
    string Risk,
    string Reason,
    string Status,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    string? DecidedBy,
    DateTime? DecidedAt,
    string? DecidedByUser = null);

public sealed record ApprovalListResponse(IReadOnlyList<ApprovalDetailDto> Approvals);

public sealed record ApprovalDetailResponse(ApprovalDetailDto Approval);
