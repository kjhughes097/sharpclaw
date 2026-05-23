using Microsoft.Extensions.Options;
using SharpClaw.Configuration;
using SharpClaw.Models;

namespace SharpClaw.Loading;

public sealed class ProjectLoader(
    IHostEnvironment env,
    IOptions<SharpClawOptions> options,
    ILogger<ProjectLoader> logger)
{
    private string ProjectsDir => ResolvePath(options.Value.ProjectsDirectory);

    public IReadOnlyList<Project> GetAllProjects()
    {
        var dir = ProjectsDir;
        if (!Directory.Exists(dir))
            return [];

        var projects = new List<Project>();
        foreach (var projectDir in Directory.EnumerateDirectories(dir))
        {
            var projectFile = Path.Combine(projectDir, "project.md");
            if (!File.Exists(projectFile))
                continue;

            var project = ParseProject(projectDir, projectFile);
            if (project is not null)
                projects.Add(project);
        }

        return projects.OrderBy(p => p.Title).ToList();
    }

    public Project? GetProject(string projectId)
    {
        var projectDir = Path.Combine(ProjectsDir, projectId);
        var projectFile = Path.Combine(projectDir, "project.md");
        if (!File.Exists(projectFile))
            return null;

        return ParseProject(projectDir, projectFile);
    }

    public Project CreateProject(string title, string? description)
    {
        var id = Slugify(title);
        var projectDir = Path.Combine(ProjectsDir, id);

        if (Directory.Exists(projectDir))
            throw new InvalidOperationException($"Project '{id}' already exists.");

        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(Path.Combine(projectDir, "tickets"));

        var now = DateTimeOffset.UtcNow;
        var project = new Project(id, title, description, now);

        WriteProjectFile(Path.Combine(projectDir, "project.md"), project);
        logger.LogInformation("Created project: {ProjectId}", id);

        return project;
    }

    public IReadOnlyList<Ticket> GetTickets(string projectId, TicketStatus? statusFilter = null)
    {
        var ticketsDir = Path.Combine(ProjectsDir, projectId, "tickets");
        if (!Directory.Exists(ticketsDir))
            return [];

        var tickets = new List<Ticket>();
        foreach (var file in Directory.EnumerateFiles(ticketsDir, "*.md").OrderBy(f => f))
        {
            var ticket = ParseTicket(projectId, file);
            if (ticket is not null)
            {
                if (statusFilter is null || ticket.Status == statusFilter)
                    tickets.Add(ticket);
            }
        }

        return tickets;
    }

    public Ticket? GetTicket(string projectId, string ticketId)
    {
        var file = ResolveTicketFile(projectId, ticketId);
        if (!File.Exists(file))
            return null;

        return ParseTicket(projectId, file);
    }

    public Ticket CreateTicket(string projectId, string title, string? description)
    {
        var ticketsDir = Path.Combine(ProjectsDir, projectId, "tickets");
        if (!Directory.Exists(ticketsDir))
            throw new InvalidOperationException($"Project '{projectId}' not found.");

        var nextId = GetNextTicketId(ticketsDir);
        var now = DateTimeOffset.UtcNow;
        var ticket = new Ticket(nextId, projectId, title, description, TicketStatus.Planning, now, now);

        var file = Path.Combine(ticketsDir, $"{nextId}.md");
        WriteTicketFile(file, ticket);
        logger.LogInformation("Created ticket {TicketId} in project {ProjectId}", nextId, projectId);

        return ticket;
    }

    public Ticket? UpdateTicket(string projectId, string ticketId, TicketStatus? status = null, string? title = null, string? description = null)
    {
        var file = ResolveTicketFile(projectId, ticketId);
        if (!File.Exists(file))
            return null;

        var existing = ParseTicket(projectId, file);
        if (existing is null)
            return null;

        var updated = existing with
        {
            Title = title ?? existing.Title,
            Description = description ?? existing.Description,
            Status = status ?? existing.Status,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        WriteTicketFile(file, updated);
        logger.LogInformation("Updated ticket {TicketId} in project {ProjectId}", ticketId, projectId);

        return updated;
    }

    private string ResolveTicketFile(string projectId, string ticketId)
    {
        var ticketsDir = Path.Combine(ProjectsDir, projectId, "tickets");
        // Support both "001" and full path
        var filename = ticketId.EndsWith(".md") ? ticketId : $"{ticketId}.md";
        return Path.Combine(ticketsDir, filename);
    }

    private static string GetNextTicketId(string ticketsDir)
    {
        var existing = Directory.EnumerateFiles(ticketsDir, "*.md")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Where(n => int.TryParse(n, out _))
            .Select(n => int.Parse(n))
            .DefaultIfEmpty(0)
            .Max();

        return (existing + 1).ToString("D3");
    }

    private static Project? ParseProject(string projectDir, string filePath)
    {
        var (frontmatter, body) = ParseFrontmatter(File.ReadAllLines(filePath));
        var id = Path.GetFileName(projectDir);
        var title = frontmatter.GetValueOrDefault("title") ?? id;
        var createdAt = ParseDateTimeOffset(frontmatter.GetValueOrDefault("created_at"));

        return new Project(id, title, body, createdAt);
    }

    private static Ticket? ParseTicket(string projectId, string filePath)
    {
        var (frontmatter, body) = ParseFrontmatter(File.ReadAllLines(filePath));
        var id = Path.GetFileNameWithoutExtension(filePath);
        var title = frontmatter.GetValueOrDefault("title") ?? "(untitled)";
        var status = TicketStatusExtensions.ParseStatus(frontmatter.GetValueOrDefault("status"));
        var createdAt = ParseDateTimeOffset(frontmatter.GetValueOrDefault("created_at"));
        var updatedAt = ParseDateTimeOffset(frontmatter.GetValueOrDefault("updated_at"));

        return new Ticket(id, projectId, title, body, status, createdAt, updatedAt);
    }

    private static (Dictionary<string, string> frontmatter, string? body) ParseFrontmatter(string[] lines)
    {
        var frontmatter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (lines.Length == 0 || lines[0].Trim() != "---")
            return (frontmatter, string.Join('\n', lines).Trim().NullIfEmpty());

        var endIndex = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                endIndex = i;
                break;
            }
        }

        if (endIndex == -1)
            return (frontmatter, string.Join('\n', lines).Trim().NullIfEmpty());

        for (var i = 1; i < endIndex; i++)
        {
            var line = lines[i];
            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0) continue;

            var key = line[..colonIdx].Trim();
            var value = line[(colonIdx + 1)..].Trim();
            frontmatter[key] = value;
        }

        var body = string.Join('\n', lines[(endIndex + 1)..]).Trim();
        return (frontmatter, body.NullIfEmpty());
    }

    private static void WriteProjectFile(string filePath, Project project)
    {
        using var writer = new StreamWriter(filePath);
        writer.WriteLine("---");
        writer.WriteLine($"title: {project.Title}");
        writer.WriteLine($"created_at: {project.CreatedAt:O}");
        writer.WriteLine("---");
        if (!string.IsNullOrEmpty(project.Description))
        {
            writer.WriteLine();
            writer.WriteLine(project.Description);
        }
    }

    private static void WriteTicketFile(string filePath, Ticket ticket)
    {
        using var writer = new StreamWriter(filePath);
        writer.WriteLine("---");
        writer.WriteLine($"title: {ticket.Title}");
        writer.WriteLine($"status: {ticket.Status.ToFrontmatterValue()}");
        writer.WriteLine($"created_at: {ticket.CreatedAt:O}");
        writer.WriteLine($"updated_at: {ticket.UpdatedAt:O}");
        writer.WriteLine("---");
        if (!string.IsNullOrEmpty(ticket.Description))
        {
            writer.WriteLine();
            writer.WriteLine(ticket.Description);
        }
    }

    private static DateTimeOffset ParseDateTimeOffset(string? value) =>
        DateTimeOffset.TryParse(value, out var result) ? result : DateTimeOffset.UtcNow;

    private static string Slugify(string title) =>
        new string(title.ToLowerInvariant()
            .Replace(' ', '-')
            .Where(c => char.IsLetterOrDigit(c) || c == '-')
            .ToArray())
        .Trim('-');

    private string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path);
}

internal static class StringExtensions
{
    public static string? NullIfEmpty(this string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}
