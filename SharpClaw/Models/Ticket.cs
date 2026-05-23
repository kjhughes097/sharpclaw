namespace SharpClaw.Models;

public sealed record Ticket(
    string Id,
    string ProjectId,
    string Title,
    string? Description,
    TicketStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);
