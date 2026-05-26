using SharpClaw.Abstractions;
using SharpClaw.Loading;
using SharpClaw.Models;

namespace SharpClaw.Tools;

public sealed class TicketTool(ProjectLoader loader) : ITool
{
    public string Name => "ticket";
    public string Description => "Manage project tickets. Actions: list_tickets, create_ticket, update_ticket, get_ticket.";

    public IReadOnlyList<ToolParameterDefinition> Parameters { get; } =
    [
        new("action", "string", "The action to perform: list_tickets, create_ticket, update_ticket, get_ticket.", Required: true),
        new("project_id", "string", "Project ID (slug). Required for all actions.", Required: true),
        new("ticket_id", "string", "Ticket ID (e.g. '001'). Required for get_ticket and update_ticket.", Required: false),
        new("title", "string", "Ticket title. Required for create_ticket, optional for update_ticket.", Required: false),
        new("description", "string", "Ticket description. Optional for create_ticket and update_ticket.", Required: false),
        new("status", "string", "Ticket status: idea, planning, in_progress, for_review, done. Optional for update_ticket.", Required: false),
    ];

    public Task<object?> ExecuteAsync(ToolCallContext context, CancellationToken ct = default)
    {
        var action = context.GetString("action").ToLowerInvariant();

        return action switch
        {
            "list_tickets" => ListTickets(context),
            "create_ticket" => CreateTicket(context),
            "update_ticket" => UpdateTicket(context),
            "get_ticket" => GetTicket(context),
            _ => Task.FromResult<object?>($"Error: Unknown action '{action}'. Use: list_tickets, create_ticket, update_ticket, get_ticket.")
        };
    }

    private Task<object?> ListTickets(ToolCallContext context)
    {
        var projectId = context.GetString("project_id");
        if (string.IsNullOrWhiteSpace(projectId))
            return Task.FromResult<object?>("Error: 'project_id' is required.");

        var statusFilter = context.GetString("status");
        TicketStatus? filter = string.IsNullOrEmpty(statusFilter) ? null : TicketStatusExtensions.ParseStatus(statusFilter);

        var tickets = loader.GetTickets(projectId, filter);
        if (tickets.Count == 0)
        {
            var msg = filter is not null
                ? $"No tickets with status '{filter.Value.ToFrontmatterValue()}' in project '{projectId}'."
                : $"No tickets in project '{projectId}'.";
            return Task.FromResult<object?>(msg);
        }

        var lines = tickets.Select(t => $"- `{t.Id}` [{t.Status.ToFrontmatterValue()}] {t.Title}");
        return Task.FromResult<object?>(string.Join('\n', lines));
    }

    private Task<object?> CreateTicket(ToolCallContext context)
    {
        var projectId = context.GetString("project_id");
        var title = context.GetString("title");
        var description = context.GetString("description");

        if (string.IsNullOrWhiteSpace(projectId))
            return Task.FromResult<object?>("Error: 'project_id' is required.");
        if (string.IsNullOrWhiteSpace(title))
            return Task.FromResult<object?>("Error: 'title' is required for create_ticket.");

        try
        {
            var ticket = loader.CreateTicket(projectId, title, string.IsNullOrEmpty(description) ? null : description);
            return Task.FromResult<object?>($"Created ticket `{ticket.Id}` in project '{projectId}': {ticket.Title}");
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult<object?>($"Error: {ex.Message}");
        }
    }

    private Task<object?> UpdateTicket(ToolCallContext context)
    {
        var projectId = context.GetString("project_id");
        var ticketId = context.GetString("ticket_id");
        var title = context.GetString("title");
        var description = context.GetString("description");
        var statusStr = context.GetString("status");

        if (string.IsNullOrWhiteSpace(projectId))
            return Task.FromResult<object?>("Error: 'project_id' is required.");
        if (string.IsNullOrWhiteSpace(ticketId))
            return Task.FromResult<object?>("Error: 'ticket_id' is required for update_ticket.");

        TicketStatus? status = string.IsNullOrEmpty(statusStr) ? null : TicketStatusExtensions.ParseStatus(statusStr);
        var newTitle = string.IsNullOrEmpty(title) ? null : title;
        var newDescription = string.IsNullOrEmpty(description) ? null : description;

        if (status is null && newTitle is null && newDescription is null)
            return Task.FromResult<object?>("Error: Provide at least one of 'status', 'title', or 'description' to update.");

        var updated = loader.UpdateTicket(projectId, ticketId, status, newTitle, newDescription);
        if (updated is null)
            return Task.FromResult<object?>($"Error: Ticket '{ticketId}' not found in project '{projectId}'.");

        return Task.FromResult<object?>($"Updated ticket `{updated.Id}`: [{updated.Status.ToFrontmatterValue()}] {updated.Title}");
    }

    private Task<object?> GetTicket(ToolCallContext context)
    {
        var projectId = context.GetString("project_id");
        var ticketId = context.GetString("ticket_id");

        if (string.IsNullOrWhiteSpace(projectId))
            return Task.FromResult<object?>("Error: 'project_id' is required.");
        if (string.IsNullOrWhiteSpace(ticketId))
            return Task.FromResult<object?>("Error: 'ticket_id' is required for get_ticket.");

        var ticket = loader.GetTicket(projectId, ticketId);
        if (ticket is null)
            return Task.FromResult<object?>($"Error: Ticket '{ticketId}' not found in project '{projectId}'.");

        var result = $"**{ticket.Title}** (`{ticket.Id}`)\nProject: {ticket.ProjectId}\nStatus: {ticket.Status.ToFrontmatterValue()}\nCreated: {ticket.CreatedAt:yyyy-MM-dd HH:mm}\nUpdated: {ticket.UpdatedAt:yyyy-MM-dd HH:mm}";
        if (!string.IsNullOrEmpty(ticket.Description))
            result += $"\n\n{ticket.Description}";

        return Task.FromResult<object?>(result);
    }
}
