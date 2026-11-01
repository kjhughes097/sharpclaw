namespace SharpClaw.Contracts;

// ----- Activity (3.3.1 spec section 3.2): recent user and assistant messages across threads. -----

/// <summary>
/// One conversational message. <c>From</c>/<c>To</c>: user name → agent for prompts, agent → user
/// name for replies (<c>user</c> when the sender wasn't recorded); for agent-to-agent messages (3.5
/// spec) the asking agent → the recipient, and the recipient → the asking agent for its reply. <c>Preview</c> is plain text, at
/// most 120 characters with whitespace collapsed.
/// </summary>
public sealed record ActivityItemDto(
    Guid MessageId,
    Guid ThreadId,
    string? ThreadTitle,
    Guid AgentId,
    string AgentName,
    string Role,
    string From,
    string To,
    string Status,
    DateTime CreatedAt,
    string Preview);

/// <summary>Newest first. Pass <c>NextCursor</c> as <c>before</c>; null on the last page.</summary>
public sealed record ActivityResponse(IReadOnlyList<ActivityItemDto> Items, string? NextCursor = null);
