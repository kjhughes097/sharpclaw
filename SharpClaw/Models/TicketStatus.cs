namespace SharpClaw.Models;

public enum TicketStatus
{
    Planning,
    InProgress,
    ForReview,
    Done
}

public static class TicketStatusExtensions
{
    public static string ToFrontmatterValue(this TicketStatus status) => status switch
    {
        TicketStatus.Planning => "planning",
        TicketStatus.InProgress => "in_progress",
        TicketStatus.ForReview => "for_review",
        TicketStatus.Done => "done",
        _ => "planning"
    };

    public static TicketStatus ParseStatus(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "planning" => TicketStatus.Planning,
        "in_progress" => TicketStatus.InProgress,
        "for_review" => TicketStatus.ForReview,
        "done" => TicketStatus.Done,
        _ => TicketStatus.Planning
    };
}
