using System.Text.Json;
using SharpClaw.Core;

namespace SharpClaw.Api.Tools;

/// <summary>
/// Filesystem tool provider — read, write, list, and search files
/// within a configured set of allowed directories.
/// </summary>
public sealed class FilesystemToolProvider : IToolProvider
{
    private readonly string[] _allowedRoots;

    public string Name => "filesystem";

    public FilesystemToolProvider(params string[] allowedRoots)
    {
        _allowedRoots = allowedRoots.Select(Path.GetFullPath).ToArray();
    }

    public IReadOnlyList<ToolSchema> GetSchemas() =>
    [
        new ToolSchema("read_file", "Read the contents of a file at the given path.",
            """{"type":"object","properties":{"path":{"type":"string","description":"Absolute or relative file path"}},"required":["path"]}"""),

        new ToolSchema("write_file", "Write content to a file, creating it if it doesn't exist.",
            """{"type":"object","properties":{"path":{"type":"string","description":"Absolute or relative file path"},"content":{"type":"string","description":"Content to write"}},"required":["path","content"]}"""),

        new ToolSchema("list_directory", "List files and directories at the given path.",
            """{"type":"object","properties":{"path":{"type":"string","description":"Directory path to list"}},"required":["path"]}"""),

        new ToolSchema("search_files", "Search for files matching a glob or text pattern.",
            """{"type":"object","properties":{"directory":{"type":"string","description":"Root directory to search in"},"pattern":{"type":"string","description":"Glob pattern (e.g. **/*.cs) or text to search for"},"text_search":{"type":"boolean","description":"If true, search file contents for the pattern"}},"required":["directory","pattern"]}"""),
    ];

    public async Task<ToolCallResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            var args = doc.RootElement;

            return call.Name switch
            {
                "read_file" => await ReadFile(args, ct),
                "write_file" => await WriteFile(args, ct),
                "list_directory" => ListDirectory(args),
                "search_files" => await SearchFiles(args, ct),
                _ => new ToolCallResult($"Unknown filesystem tool: {call.Name}", IsError: true),
            };
        }
        catch (Exception ex)
        {
            return new ToolCallResult($"Error: {ex.Message}", IsError: true);
        }
    }

    private async Task<ToolCallResult> ReadFile(JsonElement args, CancellationToken ct)
    {
        var path = ResolvePath(args.GetProperty("path").GetString()!);
        ValidatePath(path);

        if (!File.Exists(path))
            return new ToolCallResult($"File not found: {path}", IsError: true);

        var content = await File.ReadAllTextAsync(path, ct);
        // Truncate very large files
        if (content.Length > 100_000)
            content = content[..100_000] + "\n\n[...truncated at 100K characters]";

        return new ToolCallResult(content);
    }

    private async Task<ToolCallResult> WriteFile(JsonElement args, CancellationToken ct)
    {
        var path = ResolvePath(args.GetProperty("path").GetString()!);
        ValidatePath(path);

        var content = args.GetProperty("content").GetString()!;
        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(path, content, ct);
        return new ToolCallResult($"Written {content.Length} characters to {path}");
    }

    private ToolCallResult ListDirectory(JsonElement args)
    {
        var path = ResolvePath(args.GetProperty("path").GetString()!);
        ValidatePath(path);

        if (!Directory.Exists(path))
            return new ToolCallResult($"Directory not found: {path}", IsError: true);

        var entries = new List<string>();
        foreach (var dir in Directory.GetDirectories(path))
            entries.Add(Path.GetFileName(dir) + "/");
        foreach (var file in Directory.GetFiles(path))
            entries.Add(Path.GetFileName(file));

        return new ToolCallResult(string.Join("\n", entries));
    }

    private async Task<ToolCallResult> SearchFiles(JsonElement args, CancellationToken ct)
    {
        var directory = ResolvePath(args.GetProperty("directory").GetString()!);
        ValidatePath(directory);

        var pattern = args.GetProperty("pattern").GetString()!;
        var textSearch = args.TryGetProperty("text_search", out var ts) && ts.GetBoolean();

        if (!Directory.Exists(directory))
            return new ToolCallResult($"Directory not found: {directory}", IsError: true);

        if (textSearch)
        {
            // Search file contents
            var results = new List<string>();
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var content = await File.ReadAllTextAsync(file, ct);
                    if (content.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(Path.GetRelativePath(directory, file));
                        if (results.Count >= 50) break;
                    }
                }
                catch { /* skip binary/unreadable files */ }
            }
            return new ToolCallResult(results.Count == 0 ? "No matches found." : string.Join("\n", results));
        }
        else
        {
            // Glob search
            var results = Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
                .Take(100)
                .Select(f => Path.GetRelativePath(directory, f))
                .ToList();
            return new ToolCallResult(results.Count == 0 ? "No matches found." : string.Join("\n", results));
        }
    }

    private static string ResolvePath(string path) => Path.GetFullPath(path);

    private void ValidatePath(string fullPath)
    {
        if (!_allowedRoots.Any(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException($"Path '{fullPath}' is outside allowed directories.");
    }
}
