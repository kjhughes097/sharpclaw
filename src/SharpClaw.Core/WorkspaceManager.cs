using System.Text.Json;

namespace SharpClaw.Core;

/// <summary>
/// Manages the workspace filesystem: dynamically discovered categories
/// each containing project directories with README.md files and optional metadata.
/// </summary>
public sealed class WorkspaceManager
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromDays(30);

    private readonly string _workspaceRoot;

    public WorkspaceManager(string workspaceRoot)
    {
        _workspaceRoot = workspaceRoot;
        Directory.CreateDirectory(_workspaceRoot);
    }

    /// <summary>Lists all categories by scanning top-level directories.</summary>
    public IReadOnlyList<string> ListCategories()
    {
        if (!Directory.Exists(_workspaceRoot))
            return [];

        return Directory.GetDirectories(_workspaceRoot)
            .Select(Path.GetFileName)
            .Where(name => name is not null && !name.StartsWith('.') && !string.Equals(name, "memory", StringComparison.OrdinalIgnoreCase))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Lists all projects within a category.</summary>
    public IReadOnlyList<WorkspaceProject> ListProjects(string category)
    {
        var categoryDir = Path.Combine(_workspaceRoot, NormalizeCategory(category));
        if (!Directory.Exists(categoryDir))
            return [];

        return Directory.GetDirectories(categoryDir)
            .Select(dir => LoadProject(category, dir))
            .OrderByDescending(p => p.LastModifiedAt)
            .ToList();
    }

    /// <summary>Gets a single project by category and slug.</summary>
    public WorkspaceProject? GetProject(string category, string slug)
    {
        var dir = Path.Combine(_workspaceRoot, NormalizeCategory(category), slug);
        return Directory.Exists(dir) ? LoadProject(category, dir) : null;
    }

    /// <summary>Gets the README.md content for a project.</summary>
    public string? GetReadme(string category, string slug)
    {
        var readmePath = Path.Combine(_workspaceRoot, NormalizeCategory(category), slug, "README.md");
        return File.Exists(readmePath) ? File.ReadAllText(readmePath) : null;
    }

    /// <summary>Reads the status.json file from the workspace root.</summary>
    public string? ReadStatusCards()
    {
        var path = Path.Combine(_workspaceRoot, "status.json");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private WorkspaceProject LoadProject(string category, string dir)
    {
        var slug = Path.GetFileName(dir);
        var info = new DirectoryInfo(dir);
        var cat = NormalizeCategory(category);

        // Read README.md for name (first # heading) and content
        string? readme = null;
        var name = slug;
        var readmePath = Path.Combine(dir, "README.md");
        if (File.Exists(readmePath))
        {
            readme = File.ReadAllText(readmePath);
            var firstLine = readme.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(l => l.TrimStart().StartsWith('#'));
            if (firstLine is not null)
                name = firstLine.TrimStart('#', ' ');
        }

        // Read optional .sharpclaw.json metadata
        var meta = LoadMetadata(dir);

        // Determine last modified: most recent file write in the directory tree
        var lastModified = GetLastModified(dir);

        // Derive status
        var status = meta?.Status ?? DeriveStatus(lastModified);

        return new WorkspaceProject(
            Slug: slug,
            Name: meta?.Name ?? name,
            Category: cat,
            Status: status,
            CreatedAt: info.CreationTimeUtc,
            LastModifiedAt: lastModified,
            TotalTokens: meta?.TotalTokens ?? 0,
            Icon: meta?.Icon,
            Image: meta?.Image,
            Collaborators: meta?.Collaborators ?? [],
            Readme: readme);
    }

    private static DateTimeOffset GetLastModified(string dir)
    {
        var dirInfo = new DirectoryInfo(dir);
        var latest = dirInfo.LastWriteTimeUtc;

        try
        {
            foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (file.LastWriteTimeUtc > latest)
                    latest = file.LastWriteTimeUtc;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort — use directory time
        }

        return latest;
    }

    private static WorkspaceProjectStatus DeriveStatus(DateTimeOffset lastModified) =>
        DateTimeOffset.UtcNow - lastModified > StaleThreshold
            ? WorkspaceProjectStatus.Stale
            : WorkspaceProjectStatus.Live;

    private static ProjectMetadata? LoadMetadata(string dir)
    {
        var metaPath = Path.Combine(dir, ".sharpclaw.json");
        if (!File.Exists(metaPath))
            return null;

        try
        {
            var json = File.ReadAllText(metaPath);
            return JsonSerializer.Deserialize<ProjectMetadata>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeCategory(string category) =>
        category.Trim().ToLowerInvariant();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Optional metadata from .sharpclaw.json.</summary>
    private sealed record ProjectMetadata(
        string? Name = null,
        WorkspaceProjectStatus? Status = null,
        int TotalTokens = 0,
        string? Icon = null,
        string? Image = null,
        IReadOnlyList<string>? Collaborators = null);
}
