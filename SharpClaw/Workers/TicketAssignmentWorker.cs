using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SharpClaw.Abstractions;
using SharpClaw.Configuration;
using SharpClaw.Execution;
using SharpClaw.Loading;
using SharpClaw.Models;

namespace SharpClaw.Workers;

/// <summary>
/// Periodically scans projects for tickets in the <c>todo</c> state whose assignee
/// matches a registered agent. For each such ticket, moves it to <c>in_progress</c>
/// and invokes the assigned agent with a directive to either complete it
/// (transition to <c>for_review</c>) or block it (transition to <c>blocked</c>).
///
/// Each agent processes at most one ticket per tick and is locked out of further
/// processing until its current ticket invocation finishes, satisfying the
/// "one task at a time per agent" rule.
/// </summary>
public sealed class TicketAssignmentWorker(
    IOptions<TicketWorkerOptions> options,
    IAgentRegistry agentRegistry,
    ProjectLoader projectLoader,
    TicketCommentStore commentStore,
    AgentRunner runner,
    ILogger<TicketAssignmentWorker> logger) : BackgroundService
{
    private readonly TicketWorkerOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, byte> _busyAgents = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Ticket assignment worker disabled via configuration");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.PollingIntervalSeconds));
        logger.LogInformation(
            "Ticket assignment worker started — polling every {Interval}s",
            interval.TotalSeconds);

        // Stagger first run so we don't race the registry warm-up.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during ticket assignment poll");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Ticket assignment worker stopped");
    }

    private async Task ProcessOnceAsync(CancellationToken ct)
    {
        var agentNames = agentRegistry.GetAll()
            .Select(a => a.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (agentNames.Count == 0)
            return;

        var projects = projectLoader.GetAllProjects();
        if (projects.Count == 0)
            return;

        // Track which agents we have already dispatched a ticket to this tick to
        // enforce the "one task per agent per tick" rule.
        var dispatchedThisTick = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
        {
            var todoTickets = projectLoader.GetTickets(project.Id, TicketStatus.Todo);
            foreach (var ticket in todoTickets)
            {
                if (ct.IsCancellationRequested)
                    return;

                if (string.IsNullOrWhiteSpace(ticket.Assignee))
                    continue;

                var assignee = ticket.Assignee.Trim();
                if (!agentNames.Contains(assignee))
                    continue;

                if (dispatchedThisTick.Contains(assignee))
                    continue;

                // Idempotency: only one in-flight invocation per agent at a time.
                if (!_busyAgents.TryAdd(assignee, 0))
                    continue;

                var moved = projectLoader.UpdateTicket(project.Id, ticket.Id, TicketStatus.InProgress);
                if (moved is null)
                {
                    logger.LogWarning(
                        "Failed to move ticket {Project}/{Ticket} to in_progress — file disappeared",
                        project.Id, ticket.Id);
                    _busyAgents.TryRemove(assignee, out _);
                    continue;
                }

                dispatchedThisTick.Add(assignee);
                logger.LogInformation(
                    "Dispatching ticket {Project}/{Ticket} to agent {Agent}",
                    project.Id, ticket.Id, assignee);

                _ = Task.Run(() => RunTicketAsync(assignee, project.Id, moved, ct), ct);
            }
        }
    }

    private async Task RunTicketAsync(string agentName, string projectId, Ticket ticket, CancellationToken ct)
    {
        try
        {
            var agent = agentRegistry.Get(agentName);
            if (agent is null)
            {
                logger.LogWarning(
                    "Agent {Agent} disappeared between scan and dispatch for {Project}/{Ticket} — marking blocked",
                    agentName, projectId, ticket.Id);
                MarkBlocked(projectId, ticket, $"Assigned agent '{agentName}' is not registered.");
                return;
            }

            var prompt = BuildPrompt(projectId, ticket, agentName);
            var request = new AgentRunRequest(
                Prompt: prompt,
                Llm: agent.Llm,
                Model: agent.Model,
                SystemPromptOverride: agent.SystemPrompt,
                McpServerNames: agent.McpNames.Count > 0 ? agent.McpNames : null,
                ToolNames: agent.ToolNames.Count > 0 ? agent.ToolNames : null,
                LazyMcpNames: agent.LazyMcpNames.Count > 0 ? agent.LazyMcpNames : null
            );

            logger.LogInformation(
                "Invoking agent {Agent} for ticket {Project}/{Ticket}: {Title}",
                agentName, projectId, ticket.Id, ticket.Title);

            var result = await runner.RunAsync(request, ct);

            if (!result.Success)
            {
                logger.LogWarning(
                    "Agent {Agent} returned an error for ticket {Project}/{Ticket}: {Error}",
                    agentName, projectId, ticket.Id, result.Error);
                MarkBlocked(projectId, ticket, $"Agent execution failed: {result.Error ?? "unknown error"}");
                return;
            }

            // Re-read the ticket — the agent should have transitioned it via the
            // ticket tool. If it's still in_progress, treat that as "stuck".
            var latest = projectLoader.GetTicket(projectId, ticket.Id);
            if (latest is null)
            {
                logger.LogWarning(
                    "Ticket {Project}/{Ticket} no longer exists after agent run",
                    projectId, ticket.Id);
                return;
            }

            if (latest.Status == TicketStatus.InProgress)
            {
                logger.LogWarning(
                    "Agent {Agent} finished ticket {Project}/{Ticket} without updating status — marking blocked",
                    agentName, projectId, ticket.Id);
                MarkBlocked(projectId, latest,
                    "Agent completed its turn without transitioning the ticket to 'for_review' or 'blocked'. " +
                    "Human review required.");
            }
            else
            {
                logger.LogInformation(
                    "Agent {Agent} finished ticket {Project}/{Ticket} — final status: {Status}",
                    agentName, projectId, ticket.Id, latest.Status.ToFrontmatterValue());
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation(
                "Ticket {Project}/{Ticket} processing cancelled (shutdown)",
                projectId, ticket.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error processing ticket {Project}/{Ticket} for agent {Agent}",
                projectId, ticket.Id, agentName);
            MarkBlocked(projectId, ticket, $"Worker exception: {ex.Message}");
        }
        finally
        {
            _busyAgents.TryRemove(agentName, out _);
        }
    }

    private void MarkBlocked(string projectId, Ticket ticket, string reason)
    {
        try
        {
            commentStore.Add(
                ticket.Id,
                "ticket-worker",
                $"**Auto-blocked by ticket worker:** {reason}");
            projectLoader.UpdateTicket(projectId, ticket.Id, TicketStatus.Blocked);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark ticket {Project}/{Ticket} as blocked",
                projectId, ticket.Id);
        }
    }

    private string BuildPrompt(string projectId, Ticket ticket, string agentName)
    {
        var description = string.IsNullOrWhiteSpace(ticket.Description)
            ? "(no description)"
            : ticket.Description;

        var existingComments = commentStore.GetForTicket(ticket.Id);
        string commentsSection;
        if (existingComments.Count == 0)
        {
            commentsSection = "(no prior comments)";
        }
        else
        {
            var lines = existingComments.Select(c =>
                $"- `{c.Id}` **{c.Author}** ({c.CreatedUtc:yyyy-MM-dd HH:mm} UTC): {c.Content}");
            commentsSection = string.Join('\n', lines);
        }

        return $$"""
            You have been assigned ticket `{{ticket.Id}}` in project `{{projectId}}`.

            **Title:** {{ticket.Title}}

            **Ticket description (do NOT modify this):**
            {{description}}

            **Existing comments (oldest first):**
            {{commentsSection}}

            ---

            This ticket has been automatically moved to `in_progress`. Review the existing
            comments above — they are the canonical audit trail for this ticket. If the
            ticket was previously `blocked`, the most recent comments will contain the
            blocking reason and any unblocking details from a human; use them to inform
            how you resume work.

            **Important rules:**
            - **Never** modify the ticket description via `update_ticket`. The description
              is the original requirement and must remain intact.
            - All status-change context (blocking reasons, completion summaries, PR links)
              goes into **comments**, not the description.
            - Author your comments as `{{agentName}}`.

            Work on the ticket now until you reach one of these terminal states:

            1. **Complete** — add a comment summarising the work and including a link to
               the pull request (if any), then move the ticket to `for_review`:

               ```
               ticket(action="add_comment", project_id="{{projectId}}", ticket_id="{{ticket.Id}}",
                      author="{{agentName}}",
                      comment="**Ready for review.** <summary>. PR: <url>")
               ticket(action="update_ticket", project_id="{{projectId}}", ticket_id="{{ticket.Id}}",
                      status="for_review")
               ```

            2. **Blocked** — if you cannot make further progress (missing requirements,
               external dependency, ambiguity needing human input, etc):

               ```
               ticket(action="add_comment", project_id="{{projectId}}", ticket_id="{{ticket.Id}}",
                      author="{{agentName}}",
                      comment="**Blocked:** <clear explanation of what is blocking you>")
               ticket(action="update_ticket", project_id="{{projectId}}", ticket_id="{{ticket.Id}}",
                      status="blocked")
               ```

            Do not leave the ticket in `in_progress`. You only have this one turn — finish
            or block before you stop. Begin now.
            """;
    }
}
