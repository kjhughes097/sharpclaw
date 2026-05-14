using System.ComponentModel;
using ModelContextProtocol.Server;
using SharpClaw.Memory;

namespace SharpClaw.Mcp;

public sealed class MemoryMcpTools(MemoryService memory)
{
    [McpServerTool]
    [Description("Read a memory file for an agent. Returns empty string if not found.")]
    public string MemoryRead(
        [Description("Agent name whose memory to read.")] string agentName,
        [Description("Filename to read, e.g. 'memory.md'")] string file)
    {
        return memory.ReadFile(agentName, file);
    }

    [McpServerTool]
    [Description("Write to an agent's memory file. mode: 'append' or 'replace'.")]
    public string MemoryWrite(
        [Description("Agent name.")] string agentName,
        [Description("Filename to write.")] string file,
        [Description("Content to write.")] string content,
        [Description("Write mode: 'append' or 'replace'.")] string mode = "append")
    {
        // Enforce append-only for audit.md
        if (file.Equals("audit.md", StringComparison.OrdinalIgnoreCase))
        {
            memory.AppendFile(agentName, file, $"\n{content}");
            return $"Appended to {file} (audit files are always append-only).";
        }

        switch (mode)
        {
            case "append":
                memory.AppendFile(agentName, file, $"\n{content}");
                return $"Appended to {agentName}/{file}.";
            case "replace":
                memory.WriteFile(agentName, file, content);
                return $"Replaced {agentName}/{file}.";
            default:
                return $"Error: unknown mode '{mode}'. Use 'append' or 'replace'.";
        }
    }

    [McpServerTool]
    [Description("Search an agent's memory files for a text query.")]
    public string MemorySearch(
        [Description("Agent name.")] string agentName,
        [Description("Text to search for (case-insensitive).")] string query)
    {
        var results = memory.SearchMemory(agentName, query).Take(20).ToList();
        return results.Count == 0
            ? $"No results found for '{query}'."
            : string.Join('\n', results);
    }

    [McpServerTool]
    [Description("Read from the shared knowledge directory.")]
    public string KnowledgeRead(
        [Description("Filename in the knowledge directory.")] string file)
    {
        return memory.ReadKnowledge(file);
    }

    [McpServerTool]
    [Description("Write to the shared knowledge directory. mode: 'append' or 'replace'.")]
    public string KnowledgeWrite(
        [Description("Filename in the knowledge directory.")] string file,
        [Description("Content to write.")] string content,
        [Description("Write mode: 'append' or 'replace'.")] string mode = "append")
    {
        switch (mode)
        {
            case "append":
                memory.AppendKnowledge(file, $"\n{content}");
                return $"Appended to knowledge/{file}.";
            case "replace":
                memory.WriteKnowledge(file, content);
                return $"Replaced knowledge/{file}.";
            default:
                return $"Error: unknown mode '{mode}'.";
        }
    }
}
