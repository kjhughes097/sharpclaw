using System.Text.Json;

namespace SharpClaw.Contracts;

// ----- Approvals (3.2) -----

/// <summary>
/// The agent asks the human to allow a tool call. <c>Summary</c> never contains content.
/// <c>ArgsHash</c> (<see cref="ToolArgsHash"/>) binds the approval to exactly these arguments; the
/// broker only accepts an approval whose hash matches the call it is asked to run.
/// <c>Arguments</c> is sent for broker tools only: the gateway checks it against the hash and
/// renders the approval detail from it (a command, a comment), never storing the arguments.
/// </summary>
public sealed record CreateApprovalRequest(
    Guid CorrelationId,
    Guid ThreadId,
    string ToolCallId,
    string ToolName,
    string Summary,
    string Risk,
    string Reason,
    string? ArgsHash = null,
    JsonElement? Arguments = null);

/// <summary><c>GrantId</c>: the standing grant that approved it, when <c>DecidedBy</c> is <c>grant</c>.</summary>
public sealed record ApprovalDto(Guid Id, string Status, string? DecidedBy, DateTime ExpiresAt, Guid? GrantId = null);

public sealed record ApprovalResponse(ApprovalDto Approval);

/// <summary>
/// <c>Decision</c> is <c>approved</c> or <c>denied</c>. <c>Remember</c> (approved only) also creates
/// a standing grant for the tool (3.6 spec section 7).
/// </summary>
public sealed record DecideApprovalRequest(string Decision, ApprovalRememberRequest? Remember = null);

/// <summary><c>Scope</c> is <c>thread</c> or <c>agent</c>; <c>TtlHours</c> is null (until revoked), 1, 8 or 24.</summary>
public sealed record ApprovalRememberRequest(string Scope, int? TtlHours = null);

public static class ApprovalGrantScopes
{
    public const string Thread = "thread";
    public const string Agent = "agent";

    public static readonly IReadOnlyList<int> TtlHours = new[] { 1, 8, 24 };
}

/// <summary>A standing grant: calls of <c>ToolName</c> are approved without asking (3.6 spec).</summary>
public sealed record ApprovalGrantDto(
    Guid Id, string ToolName, string Scope, Guid? ThreadId, DateTime? ExpiresAt, string? CreatedByUser, DateTime CreatedAt);

public sealed record ApprovalGrantListResponse(IReadOnlyList<ApprovalGrantDto> Grants);

/// <summary>Exactly one of <c>Ids</c> (1–100) or <c>ThreadId</c>.</summary>
public sealed record BatchDecideApprovalsRequest(string Decision, IReadOnlyList<Guid>? Ids = null, Guid? ThreadId = null);

/// <summary><c>Skipped</c>: ids that were unknown or no longer pending.</summary>
public sealed record BatchDecideApprovalsResponse(IReadOnlyList<ApprovalDto> Decided, int Skipped);


/// <summary>
/// An approval as shown in the client queue. Never carries tool arguments. <c>Detail</c> is only
/// filled by <c>GET /api/approvals/{id}</c>: what the human needs to judge a broker call (the
/// command for <c>sandbox_run</c>, capped), rendered by the gateway; lists and events never carry it.
/// </summary>
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
    string? DecidedByUser = null,
    string? Detail = null,
    Guid? GrantId = null);

public sealed record ApprovalListResponse(IReadOnlyList<ApprovalDetailDto> Approvals);

public sealed record ApprovalDetailResponse(ApprovalDetailDto Approval);
