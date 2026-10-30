using System.Text;
using System.Text.Json;

namespace SharpClaw.Agent.Tools.Workspace;

/// <summary>
/// Searches file contents for literal text (agent tools spec section 4.1). Case-insensitive, line by
/// line, no regex. Results contain file content, so they are untrusted and taint the turn.
/// </summary>
internal sealed class SearchTool(WorkspaceRoot root) : IAgentTool
{
    public const int MaxQueryLength = 200;
    public const int MaxMatches = 50;
    public const int MaxFilesVisited = 2000;
    public const long MaxFileBytes = 1024 * 1024;
    public const int MaxLineChars = 200;

    public string Name => "workspace_search";
    public string Description =>
        $"Search workspace text files for a literal, case-insensitive string (at most {MaxMatches} matching lines). " +
        "Optionally limit to a directory and a glob: without '/' it matches file names (e.g. *.md), with '/' paths relative to the directory.";
    public string ParametersJson => """
        {"type":"object","properties":{"query":{"type":"string","description":"Literal text to find (1-200 characters, case-insensitive)"},"path":{"type":"string","description":"Directory to search, relative to the workspace root; omit for the root"},"glob":{"type":"string","description":"Optional file filter, e.g. *.md or notes/**/*.txt"}},"required":["query"]}
        """;
    public bool ReturnsUntrustedContent => true;

    public ToolAssessment Assess(JsonElement args)
    {
        if (root.Resolve(Args.String(args, "path"), allowRoot: true).Error is { } error)
            return new ToolAssessment.Blocked(error);
        if (Args.String(args, "query") is not { Length: > 0 and <= MaxQueryLength })
            return new ToolAssessment.Blocked($"query must be 1-{MaxQueryLength} characters");
        if (Args.String(args, "glob") is { } glob && Glob.Validate(glob, "glob") is { } globError)
            return new ToolAssessment.Blocked(globError);
        return new ToolAssessment.Scored(RiskLevel.Low, "searches file contents");
    }

    // Never the query: it's model-supplied free text.
    public string Summarize(JsonElement args)
    {
        var summary = $"search {Args.DirectoryLabel(Args.String(args, "path"))}";
        return Args.String(args, "glob") is { Length: > 0 } glob ? $"{summary} ({Args.ShortPath(glob)})" : summary;
    }

    public async Task<ToolResult> ExecuteUncheckedAsync(JsonElement args, CancellationToken ct)
    {
        var dir = root.Require(Args.String(args, "path"), allowRoot: true);
        if (!Directory.Exists(dir.FullPath))
            return ToolResult.Error("not_found", $"No directory at '{dir.Relative}'.");
        var query = Args.String(args, "query")!;
        var glob = Args.String(args, "glob");

        var matches = new List<object>();
        var visited = 0;
        var truncated = false;
        foreach (var file in WorkspaceFiles.Walk(dir.FullPath))
        {
            ct.ThrowIfCancellationRequested();
            if (glob is { Length: > 0 } && !Glob.IsMatch(glob, Glob.HasDirectory(glob) ? WorkspaceFiles.Relative(dir.FullPath, file.FullName) : file.Name))
                continue;
            if (++visited > MaxFilesVisited)
            {
                truncated = true;
                break;
            }
            if (file.Length > MaxFileBytes)
                continue;

            // Opened without following links: the walk skips them, but one could appear meanwhile.
            var relative = WorkspaceFiles.Relative(root.Path, file.FullName);
            await using var stream = NoFollow.OpenRead(root.Path, relative);
            if (WorkspaceFiles.IsBinary(stream))
                continue;
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            var lineNumber = 0;
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                lineNumber++;
                if (!line.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (matches.Count == MaxMatches)
                {
                    truncated = true;
                    break;
                }
                var text = line.Trim();
                matches.Add(new { path = relative, line = lineNumber, text = text.Length <= MaxLineChars ? text : text[..MaxLineChars] });
            }
            if (truncated)
                break;
        }

        return ToolResult.Success(new { matches, truncated });
    }
}

/// <summary>
/// Finds files whose path (relative to the search directory) matches a glob (agent tools spec
/// section 4.2). Names only, so results are not treated as untrusted, matching workspace_list.
/// </summary>
internal sealed class FindTool(WorkspaceRoot root) : IAgentTool
{
    public const int MaxResults = 200;

    public string Name => "workspace_find";
    public string Description =>
        $"Find workspace files by glob (case-insensitive; * and ? within a name, ** for any number of folders), e.g. **/*.md. At most {MaxResults} paths.";
    public string ParametersJson => """
        {"type":"object","properties":{"pattern":{"type":"string","description":"Glob matched against paths relative to the directory, e.g. **/*.md"},"path":{"type":"string","description":"Directory to search, relative to the workspace root; omit for the root"}},"required":["pattern"]}
        """;
    public bool ReturnsUntrustedContent => false;

    public ToolAssessment Assess(JsonElement args)
    {
        if (root.Resolve(Args.String(args, "path"), allowRoot: true).Error is { } error)
            return new ToolAssessment.Blocked(error);
        if (Glob.Validate(Args.String(args, "pattern"), "pattern") is { } patternError)
            return new ToolAssessment.Blocked(patternError);
        return new ToolAssessment.Scored(RiskLevel.Low, "finds files by name");
    }

    public string Summarize(JsonElement args) =>
        $"find {Args.ShortPath(Args.String(args, "pattern"))} in {Args.DirectoryLabel(Args.String(args, "path"))}";

    public Task<ToolResult> ExecuteUncheckedAsync(JsonElement args, CancellationToken ct)
    {
        var dir = root.Require(Args.String(args, "path"), allowRoot: true);
        if (!Directory.Exists(dir.FullPath))
            return Task.FromResult(ToolResult.Error("not_found", $"No directory at '{dir.Relative}'."));
        var pattern = Args.String(args, "pattern")!;

        // All matches are collected so the first MaxResults are the first in ordinal order.
        var all = WorkspaceFiles.Walk(dir.FullPath)
            .Where(f => Glob.IsMatch(pattern, WorkspaceFiles.Relative(dir.FullPath, f.FullName)))
            .Select(f => WorkspaceFiles.Relative(root.Path, f.FullName))
            .Order(StringComparer.Ordinal)
            .ToList();
        var paths = all.Take(MaxResults).ToList();
        return Task.FromResult(ToolResult.Success(new { paths, truncated = all.Count > MaxResults }));
    }
}
