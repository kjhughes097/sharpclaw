namespace SharpClaw.Models;

public sealed record TicketComment
{
    public required string Id { get; init; }
    public required string TicketId { get; init; }
    public required string Author { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedUtc { get; init; }
}
