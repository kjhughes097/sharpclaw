namespace SharpClaw.Models;

public enum TicketStatus
{
    Idea,
    Planning,
    InProgress,
    ForReview,
    Done
}

public static class TicketStatusExtensions
{
    public static string ToFrontmatterValue(this TicketStatus status) => status switch
    {
        TicketStatus.Idea => "idea",
        TicketStatus.Planning => "planning",
        TicketStatus.InProgress => "in_progress",
        TicketStatus.ForReview => "for_review",
        TicketStatus.Done => "done",
        _ => "idea"
    };

    public static TicketStatus ParseStatus(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "idea" => TicketStatus.Idea,
        "planning" => TicketStatus.Planning,
        "in_progress" => TicketStatus.InProgress,
        "for_review" => TicketStatus.ForReview,
        "done" => TicketStatus.Done,
        _ => TicketStatus.Idea
    };
}
