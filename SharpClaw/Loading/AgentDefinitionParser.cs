using SharpClaw.Models;

namespace SharpClaw.Loading;

/// <summary>
/// Parses a {name}.agent.md file (YAML frontmatter + markdown body) into an AgentDefinition.
/// </summary>
internal static class AgentDefinitionParser
{
    public static AgentDefinition Parse(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(
                       Path.GetFileNameWithoutExtension(filePath))
                   .ToLowerInvariant();

        var lines = File.ReadAllLines(filePath);

        if (lines.Length == 0 || lines[0].Trim() != "---")
            return new AgentDefinition(name, null, null, null, [], [], [], [], null, BuildPrompt(lines, 0));

        var endFrontmatter = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                endFrontmatter = i;
                break;
            }
        }

        if (endFrontmatter == -1)
            return new AgentDefinition(name, null, null, null, [], [], [], [], null, BuildPrompt(lines, 0));

        string? description = null;
        string? llm = null;
        string? model = null;
        long? telegramChatId = null;
        var tools = new List<string>();
        var mcpServers = new List<string>();
        var skills = new List<string>();
        var subAgents = new List<string>();
        string? currentKey = null;

        for (var i = 1; i < endFrontmatter; i++)
        {
            var line = lines[i];

            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && line.TrimStart().StartsWith("- "))
            {
                var item = line.TrimStart()[2..].Trim();
                switch (currentKey)
                {
                    case "tools": tools.Add(item); break;
                    case "mcp_servers": mcpServers.Add(item); break;
                    case "skills": skills.Add(item); break;
                    case "sub_agents": subAgents.Add(item); break;
                }
            }
            else if (line.Contains(':'))
            {
                var colonIdx = line.IndexOf(':');
                var key = line[..colonIdx].Trim().ToLowerInvariant();
                var value = line[(colonIdx + 1)..].Trim();
                currentKey = key;

                switch (key)
                {
                    case "description": description = value.Length > 0 ? value : null; break;
                    case "llm": llm = value.Length > 0 ? value : null; break;
                    case "model": model = value.Length > 0 ? value : null; break;
                    case "telegram_chat_id":
                        if (value.Length > 0 && long.TryParse(value, out var chatId))
                            telegramChatId = chatId;
                        break;
                    case "tools":
                        ParseInlineList(value, tools);
                        break;
                    case "mcp_servers":
                        ParseInlineList(value, mcpServers);
                        break;
                    case "skills":
                        ParseInlineList(value, skills);
                        break;
                    case "sub_agents":
                        ParseInlineList(value, subAgents);
                        break;
                }
            }
        }

        var systemPrompt = BuildPrompt(lines, endFrontmatter + 1);
        return new AgentDefinition(name, description, llm, model, tools, mcpServers, skills, subAgents, telegramChatId, systemPrompt);
    }

    private static void ParseInlineList(string value, List<string> target)
    {
        // Handle [item1, item2] syntax
        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            var items = value[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            target.AddRange(items);
        }
    }

    private static string? BuildPrompt(string[] lines, int startIndex)
    {
        if (startIndex >= lines.Length)
            return null;

        var prompt = string.Join('\n', lines[startIndex..]).Trim();
        return prompt.Length > 0 ? prompt : null;
    }
}
