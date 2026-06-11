namespace SharpClaw.Models;

public sealed record Ticket(
    string Id,
    string ProjectId,
    string Title,
    string? Description,
    TicketStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> Labels = default!,
    string? Reporter = null,
    string? Assignee = null
)
{
    public IReadOnlyList<string> Labels { get; init; } = Labels ?? [];
}
