using Microsoft.Extensions.Options;
using SharpClaw.Configuration;

namespace SharpClaw.Memory;

public sealed class MemoryService(IOptions<SharpClawOptions> options)
{
    private readonly string _workspaceRoot = options.Value.WorkspacePath;

    public string GetAgentMemoryPath(string agentName) =>
        Path.Combine(_workspaceRoot, agentName);

    public string ReadFile(string agentName, string fileName)
    {
        var path = ResolveSafePath(agentName, fileName);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    public void WriteFile(string agentName, string fileName, string content)
    {
        var path = ResolveSafePath(agentName, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void AppendFile(string agentName, string fileName, string content)
    {
        var path = ResolveSafePath(agentName, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, content);
    }

    public string ReadKnowledge(string fileName)
    {
        var path = ResolveKnowledgePath(fileName);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    public void WriteKnowledge(string fileName, string content)
    {
        var path = ResolveKnowledgePath(fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void AppendKnowledge(string fileName, string content)
    {
        var path = ResolveKnowledgePath(fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, content);
    }

    public IEnumerable<string> SearchMemory(string agentName, string query)
    {
        var agentDir = Path.Combine(_workspaceRoot, agentName);
        if (!Directory.Exists(agentDir)) yield break;

        foreach (var file in Directory.EnumerateFiles(agentDir, "*.md"))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                    yield return $"{Path.GetFileName(file)}:{i + 1}: {lines[i]}";
            }
        }
    }

    private string ResolveSafePath(string agentName, string fileName)
    {
        var agentDir = Path.Combine(_workspaceRoot, agentName);
        var full = Path.GetFullPath(Path.Combine(agentDir, fileName));
        if (!full.StartsWith(agentDir, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path traversal detected: '{fileName}'");
        return full;
    }

    private string ResolveKnowledgePath(string fileName)
    {
        var knowledgeDir = Path.Combine(_workspaceRoot, "knowledge");
        var full = Path.GetFullPath(Path.Combine(knowledgeDir, fileName));
        if (!full.StartsWith(knowledgeDir, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path traversal detected: '{fileName}'");
        return full;
    }
}
