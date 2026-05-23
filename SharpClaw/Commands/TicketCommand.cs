using SharpClaw.Loading;
using SharpClaw.Models;

namespace SharpClaw.Commands;

public sealed class TicketCommand(ProjectLoader loader) : ICommand
{
    public bool CanHandle(string text)
    {
        var trimmed = text.Trim();
        return trimmed.StartsWith(".tickets", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(".projects", StringComparison.OrdinalIgnoreCase);
    }

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        var parts = context.RawText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();

        if (command == ".projects")
            return ListProjects();

        // .tickets [project-id] [status]
        if (parts.Length == 1)
            return ListAllTicketsSummary();

        var projectId = parts[1];
        TicketStatus? statusFilter = parts.Length > 2
            ? TicketStatusExtensions.ParseStatus(parts[2])
            : null;

        return ListTickets(projectId, statusFilter);
    }

    private Task<CommandResult> ListProjects()
    {
        var projects = loader.GetAllProjects();
        if (projects.Count == 0)
            return Task.FromResult(new CommandResult(true, "No projects found."));

        var lines = projects.Select(p =>
        {
            var tickets = loader.GetTickets(p.Id);
            return $"• **{p.Title}** (`{p.Id}`) — {tickets.Count} tickets";
        });

        var response = $"Projects:\n{string.Join('\n', lines)}";
        return Task.FromResult(new CommandResult(true, response));
    }

    private Task<CommandResult> ListAllTicketsSummary()
    {
        var projects = loader.GetAllProjects();
        if (projects.Count == 0)
            return Task.FromResult(new CommandResult(true, "No projects found."));

        var lines = new List<string>();
        foreach (var project in projects)
        {
            var tickets = loader.GetTickets(project.Id);
            if (tickets.Count == 0) continue;

            lines.Add($"\n**{project.Title}** (`{project.Id}`):");
            foreach (var t in tickets)
                lines.Add($"  • `{t.Id}` [{t.Status.ToFrontmatterValue()}] {t.Title}");
        }

        var response = lines.Count > 0
            ? string.Join('\n', lines).TrimStart()
            : "No tickets found in any project.";
        return Task.FromResult(new CommandResult(true, response));
    }

    private Task<CommandResult> ListTickets(string projectId, TicketStatus? statusFilter)
    {
        var project = loader.GetProject(projectId);
        if (project is null)
            return Task.FromResult(new CommandResult(true, $"Project '{projectId}' not found."));

        var tickets = loader.GetTickets(projectId, statusFilter);
        if (tickets.Count == 0)
        {
            var msg = statusFilter is not null
                ? $"No '{statusFilter.Value.ToFrontmatterValue()}' tickets in '{projectId}'."
                : $"No tickets in '{projectId}'.";
            return Task.FromResult(new CommandResult(true, msg));
        }

        var lines = tickets.Select(t => $"• `{t.Id}` [{t.Status.ToFrontmatterValue()}] {t.Title}");
        var response = $"Tickets in **{project.Title}**:\n{string.Join('\n', lines)}";
        return Task.FromResult(new CommandResult(true, response));
    }
}
