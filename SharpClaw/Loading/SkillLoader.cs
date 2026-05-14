using Microsoft.Extensions.Options;
using SharpClaw.Configuration;
using SharpClaw.Models;

namespace SharpClaw.Loading;

public sealed class SkillLoader(
    IHostEnvironment env,
    IOptions<SharpClawOptions> options,
    ILogger<SkillLoader> logger)
{
    public IReadOnlyList<SkillDefinition> Load()
    {
        var dir = ResolvePath(options.Value.SkillsDirectory);

        if (!Directory.Exists(dir))
        {
            logger.LogDebug("Skills directory not found at {Dir} — no skills loaded", dir);
            return [];
        }

        var skills = new List<SkillDefinition>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.skill.md"))
        {
            var skill = ParseSkillFile(file);
            if (skill is not null)
            {
                skills.Add(skill);
                logger.LogDebug("Loaded skill: {Name}", skill.Name);
            }
        }

        return skills;
    }

    private SkillDefinition? ParseSkillFile(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(
                       Path.GetFileNameWithoutExtension(filePath))
                   .ToLowerInvariant();

        var lines = File.ReadAllLines(filePath);

        if (lines.Length == 0 || lines[0].Trim() != "---")
            return new SkillDefinition(name, null, BuildBody(lines, 0), null, null);

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
            return new SkillDefinition(name, null, BuildBody(lines, 0), null, null);

        string? description = null;
        string? command = null;
        var args = new List<string>();
        string? currentKey = null;

        for (var i = 1; i < endFrontmatter; i++)
        {
            var line = lines[i];

            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && line.TrimStart().StartsWith("- "))
            {
                var item = line.TrimStart()[2..].Trim();
                if (currentKey == "args") args.Add(item);
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
                    case "command": command = value.Length > 0 ? value : null; break;
                    case "args":
                        if (value.StartsWith('[') && value.EndsWith(']'))
                        {
                            var items = value[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            args.AddRange(items);
                        }
                        break;
                }
            }
        }

        var promptText = BuildBody(lines, endFrontmatter + 1);
        return new SkillDefinition(name, description, promptText, command, args.Count > 0 ? args : null);
    }

    private static string BuildBody(string[] lines, int startIndex)
    {
        if (startIndex >= lines.Length)
            return string.Empty;

        return string.Join('\n', lines[startIndex..]).Trim();
    }

    private string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path);
}
