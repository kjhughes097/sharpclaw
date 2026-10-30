using System.Text.Json;

namespace SharpClaw.Agent.Tools.Workspace;

/// <summary>
/// Confines paths to the agent's workspace (3.2 spec section 6, hardline rules). Paths are relative
/// and '/'-separated; absolute paths, anything resolving outside the root, and symbolic links are
/// refused. Docker isolation is the outer boundary; this keeps tools from even trying.
/// </summary>
public sealed class WorkspaceRoot
{
    public WorkspaceRoot(string root) =>
        Path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));

    public string Path { get; }

    /// <summary>A resolved path inside the workspace, plus the relative form shown to the model.</summary>
    public sealed record Resolved(string FullPath, string Relative);

    /// <summary>
    /// Resolves <paramref name="relative"/>; returns an error reason instead when it breaks a rule.
    /// An empty path means the root, which only listing allows.
    /// </summary>
    public (Resolved? Path, string? Error) Resolve(string? relative, bool allowRoot = false)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Trim() is "." or "./")
            return allowRoot ? (new Resolved(Path, "."), null) : (null, "a path inside the workspace is required");

        if (relative.Contains('\0'))
            return (null, "path contains a null character");
        var normalised = relative.Replace('\\', '/');
        if (normalised.StartsWith('/') || System.IO.Path.IsPathRooted(normalised))
            return (null, "absolute paths are not allowed; use a path relative to the workspace");

        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, normalised));
        var inside = full == Path || full.StartsWith(Path + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
        if (!inside)
            return (null, "path resolves outside the workspace");
        if (full == Path && !allowRoot)
            return (null, "a path inside the workspace is required");

        if (FirstSymlink(full) is { } link)
            return (null, $"symbolic links are not allowed ({link})");

        var rel = full == Path ? "." : System.IO.Path.GetRelativePath(Path, full).Replace('\\', '/');
        return (new Resolved(full, rel), null);
    }

    /// <summary>
    /// <see cref="Resolve"/> for the execute step: the call was assessed already, so a rule broken
    /// now means the workspace changed in between (e.g. a link appeared) and the call must fail.
    /// </summary>
    public Resolved Require(string? relative, bool allowRoot = false)
    {
        var (path, error) = Resolve(relative, allowRoot);
        return path ?? throw new WorkspacePathException(error!);
    }

    /// <summary>The first existing component under the root that is a symbolic link, if any.</summary>
    private string? FirstSymlink(string full)
    {
        var current = Path;
        foreach (var part in System.IO.Path.GetRelativePath(Path, full).Split(System.IO.Path.DirectorySeparatorChar))
        {
            if (part is "" or ".")
                continue;
            current = System.IO.Path.Combine(current, part);

            // FileInfo reports LinkTarget for links to files, directories and dangling links alike.
            var info = new FileInfo(current);
            if (info.LinkTarget is not null)
                return System.IO.Path.GetRelativePath(Path, current).Replace('\\', '/');
            if (!info.Exists && !Directory.Exists(current))
                return null; // nothing further down exists yet
        }
        return null;
    }
}

internal static class Args
{
    public static string? String(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    /// <summary>Paths in summaries are capped so a long path can't swamp the approval prompt.</summary>
    public static string ShortPath(string? path) =>
        string.IsNullOrEmpty(path) ? "." : path.Length <= 120 ? path : path[..117] + "...";

    /// <summary>A directory argument for summaries: "notes/", or "./" for the root.</summary>
    public static string DirectoryLabel(string? path) =>
        string.IsNullOrWhiteSpace(path) || path.Trim() is "." or "./" ? "./" : ShortPath(path.TrimEnd('/', '\\')) + "/";
}
