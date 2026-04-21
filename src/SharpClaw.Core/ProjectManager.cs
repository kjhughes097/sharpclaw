namespace SharpClaw.Core;

/// <summary>
/// Manages project folders on disk. Each project is a directory under the configured
/// projects root, containing context.md, log.md, and a chats/ subdirectory.
/// </summary>
public sealed class ProjectManager
{
    private readonly string _projectsRoot;

    public ProjectManager(string projectsRoot)
    {
        _projectsRoot = projectsRoot;
        EnsureGeneralProject();
    }

    /// <summary>Lists all projects by scanning directories.</summary>
    public IReadOnlyList<ProjectInfo> ListProjects()
    {
        if (!Directory.Exists(_projectsRoot))
            return [];

        return Directory.GetDirectories(_projectsRoot)
            .Select(LoadProjectInfo)
            .Where(p => p is not null)
            .Select(p => p!)
            .OrderBy(p => p.Slug, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Gets a single project by slug.</summary>
    public ProjectInfo? GetProject(string slug)
    {
        var dir = Path.Combine(_projectsRoot, slug);
        return Directory.Exists(dir) ? LoadProjectInfo(dir) : null;
    }

    /// <summary>Creates a new project folder with initial files.</summary>
    public ProjectInfo CreateProject(string name)
    {
        var slug = Slugify(name);
        var dir = Path.Combine(_projectsRoot, slug);

        if (Directory.Exists(dir))
            throw new InvalidOperationException($"Project '{slug}' already exists.");

        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "chats"));

        var now = DateTimeOffset.UtcNow;
        File.WriteAllText(Path.Combine(dir, "context.md"), $"""
            # Project: {name}
            Created: {now:yyyy-MM-dd}

            ## Goals


            ## Current State


            ## Recent Activity

            """);

        File.WriteAllText(Path.Combine(dir, "log.md"), $"# {name} — Interaction Log\n\n");

        return new ProjectInfo(slug, name, now, []);
    }

    /// <summary>Deletes a project and all its contents.</summary>
    public bool DeleteProject(string slug)
    {
        if (string.Equals(slug, "general", StringComparison.OrdinalIgnoreCase))
            return false;

        var dir = Path.Combine(_projectsRoot, slug);
        if (!Directory.Exists(dir))
            return false;

        Directory.Delete(dir, recursive: true);
        return true;
    }

    public string GetProjectPath(string projectSlug) => Path.Combine(_projectsRoot, projectSlug);

    private void EnsureGeneralProject()
    {
        var generalDir = Path.Combine(_projectsRoot, "general");
        if (Directory.Exists(generalDir))
            return;

        Directory.CreateDirectory(generalDir);
        Directory.CreateDirectory(Path.Combine(generalDir, "chats"));

        File.WriteAllText(Path.Combine(generalDir, "context.md"), """
            # Project: General
            Created: 2026-04-13

            ## Purpose
            Default project for ad-hoc conversations that don't belong to a specific project.
            """);

        File.WriteAllText(Path.Combine(generalDir, "log.md"), "# General — Interaction Log\n\n");
    }

    private ProjectInfo? LoadProjectInfo(string dir)
    {
        var slug = Path.GetFileName(dir);
        var info = new DirectoryInfo(dir);
        var chatsDir = Path.Combine(dir, "chats");
        var chats = Directory.Exists(chatsDir)
            ? Directory.GetDirectories(chatsDir)
                .Select(ChatManager.LoadChatInfo)
                .Where(c => c is not null)
                .Select(c => c!)
                .OrderByDescending(c => c.LastActivityAt)
                .ToList()
            : [];

        // Try to extract name from context.md first line
        var contextPath = Path.Combine(dir, "context.md");
        var name = slug;
        if (File.Exists(contextPath))
        {
            var firstLine = File.ReadLines(contextPath).FirstOrDefault() ?? "";
            if (firstLine.StartsWith("# Project: "))
                name = firstLine["# Project: ".Length..].Trim();
        }

        return new ProjectInfo(slug, name, info.CreationTimeUtc, chats,
            TotalInputTokens: chats.Sum(c => c.TotalInputTokens),
            TotalOutputTokens: chats.Sum(c => c.TotalOutputTokens));
    }

    private static string Slugify(string name)
    {
        var chars = new List<char>();
        var prevDash = false;

        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                chars.Add(ch);
                prevDash = false;
            }
            else if (!prevDash && chars.Count > 0)
            {
                chars.Add('-');
                prevDash = true;
            }
        }

        var slug = new string(chars.ToArray()).TrimEnd('-');
        return string.IsNullOrWhiteSpace(slug) ? "project" : slug;
    }
}
