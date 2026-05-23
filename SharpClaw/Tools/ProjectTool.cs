using SharpClaw.Abstractions;
using SharpClaw.Loading;
using SharpClaw.Models;

namespace SharpClaw.Tools;

public sealed class ProjectTool(ProjectLoader loader) : ITool
{
    public string Name => "project";
    public string Description => "Manage projects. Actions: list_projects, create_project, get_project.";

    public IReadOnlyList<ToolParameterDefinition> Parameters { get; } =
    [
        new("action", "string", "The action to perform: list_projects, create_project, get_project.", Required: true),
        new("project_id", "string", "Project ID (slug). Required for get_project.", Required: false),
        new("title", "string", "Project title. Required for create_project.", Required: false),
        new("description", "string", "Project description. Optional for create_project.", Required: false),
    ];

    public Task<object?> ExecuteAsync(ToolCallContext context, CancellationToken ct = default)
    {
        var action = context.GetString("action").ToLowerInvariant();

        return action switch
        {
            "list_projects" => ListProjects(),
            "create_project" => CreateProject(context),
            "get_project" => GetProject(context),
            _ => Task.FromResult<object?>($"Error: Unknown action '{action}'. Use: list_projects, create_project, get_project.")
        };
    }

    private Task<object?> ListProjects()
    {
        var projects = loader.GetAllProjects();
        if (projects.Count == 0)
            return Task.FromResult<object?>("No projects found.");

        var lines = projects.Select(p =>
        {
            var tickets = loader.GetTickets(p.Id);
            var statusCounts = tickets.GroupBy(t => t.Status)
                .ToDictionary(g => g.Key.ToFrontmatterValue(), g => g.Count());
            var summary = statusCounts.Count > 0
                ? string.Join(", ", statusCounts.Select(kv => $"{kv.Key}: {kv.Value}"))
                : "no tickets";
            return $"- **{p.Title}** (`{p.Id}`) — {summary}";
        });

        return Task.FromResult<object?>(string.Join('\n', lines));
    }

    private Task<object?> CreateProject(ToolCallContext context)
    {
        var title = context.GetString("title");
        var description = context.GetString("description");

        if (string.IsNullOrWhiteSpace(title))
            return Task.FromResult<object?>("Error: 'title' is required for create_project.");

        try
        {
            var project = loader.CreateProject(title, string.IsNullOrEmpty(description) ? null : description);
            return Task.FromResult<object?>($"Created project '{project.Title}' (id: `{project.Id}`).");
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult<object?>($"Error: {ex.Message}");
        }
    }

    private Task<object?> GetProject(ToolCallContext context)
    {
        var projectId = context.GetString("project_id");
        if (string.IsNullOrWhiteSpace(projectId))
            return Task.FromResult<object?>("Error: 'project_id' is required for get_project.");

        var project = loader.GetProject(projectId);
        if (project is null)
            return Task.FromResult<object?>($"Error: Project '{projectId}' not found.");

        var tickets = loader.GetTickets(projectId);
        var result = $"**{project.Title}** (`{project.Id}`)\nCreated: {project.CreatedAt:yyyy-MM-dd}";
        if (!string.IsNullOrEmpty(project.Description))
            result += $"\n\n{project.Description}";

        if (tickets.Count > 0)
        {
            result += $"\n\n### Tickets ({tickets.Count})";
            foreach (var t in tickets)
                result += $"\n- `{t.Id}` [{t.Status.ToFrontmatterValue()}] {t.Title}";
        }

        return Task.FromResult<object?>(result);
    }
}
