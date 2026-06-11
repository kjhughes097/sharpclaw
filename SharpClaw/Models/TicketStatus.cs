namespace SharpClaw.Models;

public enum TicketStatus
{
    Idea,
    Planning,
    Todo,
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
        TicketStatus.Todo => "todo",
        TicketStatus.InProgress => "in_progress",
        TicketStatus.ForReview => "for_review",
        TicketStatus.Done => "done",
        _ => "idea"
    };

    public static TicketStatus ParseStatus(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "idea" => TicketStatus.Idea,
        "planning" => TicketStatus.Planning,
        "todo" => TicketStatus.Todo,
        "in_progress" => TicketStatus.InProgress,
        "for_review" => TicketStatus.ForReview,
        "done" => TicketStatus.Done,
        _ => TicketStatus.Idea
    };
}
