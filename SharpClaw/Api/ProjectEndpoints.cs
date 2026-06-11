using SharpClaw.Abstractions;
using SharpClaw.Execution;
using SharpClaw.Loading;
using SharpClaw.Models;

namespace SharpClaw.Api;

internal static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects").WithTags("Projects");

        // Users (agents + configured humans) for assignment dropdowns
        app.MapGet("/api/users", (IAgentRegistry agentRegistry) =>
        {
            var agents = agentRegistry.GetAll().Select(a => new { id = a.Name, name = a.Name, type = "agent" });
            var humans = new[] { new { id = "kevin", name = "Kevin", type = "human" } };
            return humans.Concat(agents);
        }).WithTags("Users");

        group.MapGet("/", (ProjectLoader loader) =>
            loader.GetAllProjects().Select(p => new
            {
                p.Id,
                p.Title,
                p.Description,
                p.CreatedAt,
                TicketCount = loader.GetTickets(p.Id).Count,
            }));

        group.MapGet("/{projectId}", (string projectId, ProjectLoader loader) =>
        {
            var project = loader.GetProject(projectId);
            if (project is null) return Results.NotFound();

            var tickets = loader.GetTickets(projectId);
            return Results.Ok(new
            {
                project.Id,
                project.Title,
                project.Description,
                project.CreatedAt,
                Tickets = tickets.Select(t => new
                {
                    t.Id,
                    t.Title,
                    Status = t.Status.ToFrontmatterValue(),
                    t.CreatedAt,
                    t.UpdatedAt,
                }),
            });
        });

        group.MapPost("/", (CreateProjectRequest req, ProjectLoader loader) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest("Title is required.");

            try
            {
                var project = loader.CreateProject(req.Title, req.Description);
                return Results.Created($"/api/projects/{project.Id}", new
                {
                    project.Id,
                    project.Title,
                    project.Description,
                    project.CreatedAt,
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        // Tickets
        group.MapGet("/{projectId}/tickets", (string projectId, string? status, ProjectLoader loader) =>
        {
            TicketStatus? filter = string.IsNullOrEmpty(status) ? null : TicketStatusExtensions.ParseStatus(status);
            var tickets = loader.GetTickets(projectId, filter);
            return tickets.Select(t => new
            {
                t.Id,
                t.ProjectId,
                t.Title,
                t.Description,
                Status = t.Status.ToFrontmatterValue(),
                t.Labels,
                t.Reporter,
                t.Assignee,
                t.CreatedAt,
                t.UpdatedAt,
            });
        });

        group.MapGet("/{projectId}/tickets/{ticketId}", (string projectId, string ticketId, ProjectLoader loader) =>
        {
            var ticket = loader.GetTicket(projectId, ticketId);
            if (ticket is null) return Results.NotFound();

            return Results.Ok(new
            {
                ticket.Id,
                ticket.ProjectId,
                ticket.Title,
                ticket.Description,
                Status = ticket.Status.ToFrontmatterValue(),
                ticket.Labels,
                ticket.Reporter,
                ticket.Assignee,
                ticket.CreatedAt,
                ticket.UpdatedAt,
            });
        });

        group.MapPost("/{projectId}/tickets", (string projectId, CreateTicketRequest req, ProjectLoader loader) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest("Title is required.");

            try
            {
                var ticket = loader.CreateTicket(projectId, req.Title, req.Description, req.Reporter, req.Assignee, req.Labels);
                return Results.Created($"/api/projects/{projectId}/tickets/{ticket.Id}", new
                {
                    ticket.Id,
                    ticket.ProjectId,
                    ticket.Title,
                    ticket.Description,
                    Status = ticket.Status.ToFrontmatterValue(),
                    ticket.Labels,
                    ticket.Reporter,
                    ticket.Assignee,
                    ticket.CreatedAt,
                    ticket.UpdatedAt,
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapPatch("/{projectId}/tickets/{ticketId}", (string projectId, string ticketId, UpdateTicketRequest req, ProjectLoader loader) =>
        {
            var status = string.IsNullOrEmpty(req.Status) ? null : (TicketStatus?)TicketStatusExtensions.ParseStatus(req.Status);
            var labels = req.Labels is not null ? (IReadOnlyList<string>)req.Labels : null;
            var updated = loader.UpdateTicket(projectId, ticketId, status, req.Title, req.Description, req.Assignee, labels);

            if (updated is null) return Results.NotFound();

            return Results.Ok(new
            {
                updated.Id,
                updated.ProjectId,
                updated.Title,
                updated.Description,
                Status = updated.Status.ToFrontmatterValue(),
                updated.Labels,
                updated.Reporter,
                updated.Assignee,
                updated.CreatedAt,
                updated.UpdatedAt,
            });
        });

        group.MapPost("/{projectId}/tickets/{ticketId}/improve", async (
            string projectId,
            string ticketId,
            ProjectLoader loader,
            AgentRunner runner,
            CancellationToken ct) =>
        {
            var ticket = loader.GetTicket(projectId, ticketId);
            if (ticket is null) return Results.NotFound();

            var prompt = $"""
                You are a technical project manager. Improve the following ticket to make it clearer, more actionable, and complete.

                **Title:** {ticket.Title}
                **Status:** {ticket.Status.ToFrontmatterValue()}
                **Description:**
                {ticket.Description ?? "(empty)"}

                Rewrite the description in markdown. Make it:
                - Clear and concise
                - Well-structured with headings/bullet points where appropriate
                - Include acceptance criteria if not already present
                - Highlight any missing requirements or information that would prevent the task being completed (put these in a "## Questions / Missing Information" section)

                Return ONLY the improved markdown description — no preamble, no wrapping code fences.
                """;

            var request = new AgentRunRequest(
                Prompt: prompt,
                Model: "claude-haiku-4.5",
                ToolNames: [],
                McpServerNames: []);

            var result = await runner.RunAsync(request, ct);

            if (!result.Success)
                return Results.StatusCode(502);

            return Results.Ok(new { description = result.Response });
        });
    }

    private sealed record CreateProjectRequest(string Title, string? Description);
    private sealed record CreateTicketRequest(string Title, string? Description, string? Reporter, string? Assignee, string[]? Labels);
    private sealed record UpdateTicketRequest(string? Title, string? Description, string? Status, string? Assignee, string[]? Labels);
}
