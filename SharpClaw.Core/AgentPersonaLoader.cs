using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SharpClaw.Core;

/// <summary>
/// Reads an .agent.md file (YAML frontmatter + markdown body) and produces an <see cref="AgentPersona"/>.
/// </summary>
public static class AgentPersonaLoader
{
    private const string FrontmatterDelimiter = "---";

    /// <summary>
    /// Loads an <see cref="AgentPersona"/> from the given .agent.md file path.
    /// </summary>
    public static AgentPersona Load(string filePath)
    {
        var text = File.ReadAllText(filePath);
        return Parse(text);
    }

    /// <summary>
    /// Parses raw .agent.md content into an <see cref="AgentPersona"/>.
    /// </summary>
    public static AgentPersona Parse(string content)
    {
        var (yaml, body) = SplitFrontmatter(content);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var frontmatter = deserializer.Deserialize<Frontmatter>(yaml) ?? new Frontmatter();

        var permissionPolicy = new Dictionary<string, ToolPermission>();
        if (frontmatter.PermissionPolicy is not null)
        {
            foreach (var (pattern, value) in frontmatter.PermissionPolicy)
            {
                var normalized = value.Replace("_", "");
                if (Enum.TryParse<ToolPermission>(normalized, ignoreCase: true, out var perm))
                    permissionPolicy[pattern] = perm;
                else
                    throw new FormatException($"Unknown permission '{value}' for tool pattern '{pattern}'. Expected: auto_approve, ask, or deny.");
            }
        }

        return new AgentPersona(
            Name: frontmatter.Name ?? Path.GetFileNameWithoutExtension("agent"),
            SystemPrompt: body.Trim(),
            McpServers: frontmatter.McpServers ?? [],
            PermissionPolicy: permissionPolicy);
    }

    private static (string Yaml, string Body) SplitFrontmatter(string content)
    {
        var span = content.AsSpan().TrimStart();

        if (!span.StartsWith(FrontmatterDelimiter))
            return (string.Empty, content);

        // Skip past the opening "---" line.
        var afterOpen = span[FrontmatterDelimiter.Length..];
        var newline = afterOpen.IndexOfAny('\r', '\n');
        if (newline < 0)
            return (string.Empty, content);

        // Skip \r\n or \n
        if (newline < afterOpen.Length - 1 && afterOpen[newline] == '\r' && afterOpen[newline + 1] == '\n')
            newline += 2;
        else
            newline += 1;

        var rest = afterOpen[newline..];

        // Find the closing "---"
        var closeIdx = IndexOfLine(rest, FrontmatterDelimiter);
        if (closeIdx < 0)
            return (string.Empty, content);

        var yaml = rest[..closeIdx].ToString();

        var afterClose = rest[(closeIdx + FrontmatterDelimiter.Length)..];
        // Skip past the closing delimiter line.
        var bodyStart = afterClose.IndexOfAny('\r', '\n');
        string body;
        if (bodyStart < 0)
            body = string.Empty;
        else
        {
            if (bodyStart < afterClose.Length - 1 && afterClose[bodyStart] == '\r' && afterClose[bodyStart + 1] == '\n')
                bodyStart += 2;
            else
                bodyStart += 1;
            body = afterClose[bodyStart..].ToString();
        }

        return (yaml, body);
    }

    /// <summary>
    /// Returns the char index where a line consisting solely of <paramref name="marker"/> begins.
    /// </summary>
    private static int IndexOfLine(ReadOnlySpan<char> text, string marker)
    {
        var idx = 0;
        while (idx < text.Length)
        {
            var lineEnd = text[idx..].IndexOfAny('\r', '\n');
            var line = lineEnd < 0 ? text[idx..] : text[idx..(idx + lineEnd)];

            if (line.SequenceEqual(marker.AsSpan()))
                return idx;

            if (lineEnd < 0)
                break;

            idx += lineEnd;
            if (idx < text.Length && text[idx] == '\r') idx++;
            if (idx < text.Length && text[idx] == '\n') idx++;
        }

        return -1;
    }

    private sealed class Frontmatter
    {
        public string? Name { get; set; }
        public List<string>? McpServers { get; set; }
        public Dictionary<string, string>? PermissionPolicy { get; set; }
    }
}
