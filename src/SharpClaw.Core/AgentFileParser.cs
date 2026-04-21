using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SharpClaw.Core;

/// <summary>
/// Parses *.agent.md files into <see cref="AgentDefinition"/> instances.
/// Files use YAML frontmatter between --- delimiters, with the system prompt in the body.
/// </summary>
public static class AgentFileParser
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Parses all *.agent.md files from the given directory.
    /// </summary>
    public static IReadOnlyList<AgentDefinition> LoadAll(string agentsDirectory)
    {
        if (!Directory.Exists(agentsDirectory))
            return [];

        return Directory.GetFiles(agentsDirectory, "*.agent.md")
            .Select(ParseFile)
            .Where(a => a is not null)
            .Select(a => a!)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Parses a single *.agent.md file. Returns null if parsing fails.
    /// </summary>
    public static AgentDefinition? ParseFile(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        var content = File.ReadAllText(filePath);
        return Parse(content, Path.GetFileNameWithoutExtension(filePath).Replace(".agent", ""));
    }

    /// <summary>
    /// Parses agent markdown content with YAML frontmatter.
    /// </summary>
    public static AgentDefinition? Parse(string content, string fallbackSlug)
    {
        var (yaml, body) = SplitFrontmatter(content);
        if (yaml is null)
            return null;

        try
        {
            var frontmatter = YamlDeserializer.Deserialize<AgentFrontmatter>(yaml);
            if (string.IsNullOrWhiteSpace(frontmatter.Name))
                return null;

            var slug = CreateSlug(frontmatter.Name) ?? fallbackSlug;
            var systemPrompt = body?.Trim() ?? string.Empty;

            return new AgentDefinition(
                Slug: slug,
                Name: frontmatter.Name.Trim(),
                Description: frontmatter.Description?.Trim() ?? string.Empty,
                Service: frontmatter.Service?.Trim().ToLowerInvariant() ?? "llm",
                Model: frontmatter.Model?.Trim() ?? string.Empty,
                Tools: frontmatter.Tools ?? [],
                SystemPrompt: systemPrompt);
        }
        catch
        {
            return null;
        }
    }

    private static (string? Yaml, string? Body) SplitFrontmatter(string content)
    {
        if (!content.StartsWith("---"))
            return (null, content);

        var endIndex = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIndex < 0)
            return (null, content);

        var yaml = content[3..endIndex].Trim();
        var body = content[(endIndex + 4)..];
        return (yaml, body);
    }

    private static string? CreateSlug(string name)
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
        return string.IsNullOrWhiteSpace(slug) ? null : slug;
    }

    private sealed class AgentFrontmatter
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Service { get; set; }
        public string? Model { get; set; }
        public List<string>? Tools { get; set; }
    }
}
