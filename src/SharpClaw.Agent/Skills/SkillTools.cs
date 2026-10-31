using System.Text.Json;
using SharpClaw.Agent.Tools;
using SharpClaw.Agent.Tools.Workspace;

namespace SharpClaw.Agent.Skills;

/// <summary>
/// The skill tool (skills spec section 7). Enabling it in agent.json turns skills on (Q8). It only
/// reads files from the operator's read-only library; it never runs or interprets them.
/// </summary>
public static class SkillTools
{
    public const string ReadName = "skill_read";

    /// <summary>At most this many other file paths are listed when SKILL.md is read.</summary>
    public const int MaxListedFiles = 100;

    public static IReadOnlyCollection<string> Names { get; } = new[] { ReadName };

    public static IAgentTool[] All(SkillLibrary library) => new IAgentTool[] { new ReadTool(library) };

    /// <summary>
    /// Reads a file of a loaded skill, paged like <c>workspace_read</c>. Paths follow the workspace
    /// hardline rules rooted at the skill's directory. Skills are operator-curated and mounted
    /// read-only, the same trust as the soul, so results don't taint the turn (Q7).
    /// </summary>
    private sealed class ReadTool(SkillLibrary library) : IAgentTool
    {
        public string Name => ReadName;
        public string Description =>
            "Load a skill's instructions (its SKILL.md, the default) or another file in the skill, at most " +
            $"{TextPaging.MaxPageBytes / 1024} KB per call. Reading SKILL.md also lists the skill's other files. " +
            "When the result is truncated, call again with offset = nextOffset.";
        public string ParametersJson => """
            {"type":"object","properties":{"name":{"type":"string","description":"The skill's name, from the skills list"},"path":{"type":"string","description":"File path relative to the skill folder (default SKILL.md)"},"offset":{"type":"integer","minimum":0,"description":"Byte offset to start reading from (default 0); use nextOffset from a truncated result"}},"required":["name"]}
            """;
        public bool ReturnsUntrustedContent => false;

        public ToolAssessment Assess(JsonElement args)
        {
            if (!TryPath(args, out var relative))
                return new ToolAssessment.Blocked("path must be a string");
            if (!TextPaging.TryOffset(args, out _))
                return new ToolAssessment.Blocked("offset must be a non-negative integer");

            // An unknown name isn't a hardline rule: the call runs and reports unknown_skill.
            if (library.TryGet(Args.String(args, "name"), out var skill) &&
                Root(skill).Resolve(relative).Error is { } error)
                return new ToolAssessment.Blocked(error);
            return new ToolAssessment.Scored(RiskLevel.Low, "reads a skill file");
        }

        public string Summarize(JsonElement args)
        {
            TryPath(args, out var relative);
            return $"read skill {Args.ShortPath($"{Args.String(args, "name")}/{relative}")}";
        }

        public async Task<ToolResult> ExecuteUncheckedAsync(JsonElement args, CancellationToken ct)
        {
            if (!library.TryGet(Args.String(args, "name"), out var skill))
                return ToolResult.Error("unknown_skill", "No skill with that name; use a name from the skills list.");

            TryPath(args, out var relative);
            var root = Root(skill);
            var path = root.Require(relative);
            if (Directory.Exists(path.FullPath))
                return ToolResult.Error("not_a_file", $"'{path.Relative}' is a folder; read a file in it.");
            if (!File.Exists(path.FullPath))
                return ToolResult.Error("not_found", $"No file at '{path.Relative}' in skill {skill.Name}.");

            await using var stream = NoFollow.OpenRead(root.Path, path.Relative);
            if (WorkspaceFiles.IsBinary(stream))
                return ToolResult.Error("binary_file", $"'{path.Relative}' is not a text file.");

            TextPaging.TryOffset(args, out var requested);
            var page = await TextPaging.ReadPageAsync(stream, requested, ct);
            var result = new Dictionary<string, object?>
            {
                ["name"] = skill.Name,
                ["path"] = path.Relative,
                ["content"] = page.Content,
                ["offset"] = page.Offset,
                ["truncated"] = page.Truncated,
            };
            if (page.Truncated)
                result["nextOffset"] = page.NextOffset;

            // A discovery hint, so the model knows what else it can load without a listing tool.
            // Hidden folders are skipped: a cloned skill's .git would otherwise fill the cap.
            if (path.Relative == SkillLibrary.SkillFile)
                result["files"] = WorkspaceFiles.Walk(skill.Directory, skipHidden: true)
                    .Select(f => WorkspaceFiles.Relative(skill.Directory, f.FullName))
                    .Where(f => f != SkillLibrary.SkillFile)
                    .Take(MaxListedFiles)
                    .ToList();
            return ToolResult.Success(result);
        }

        private static WorkspaceRoot Root(Skill skill) => new(skill.Directory, "skill");

        /// <summary>The path argument; SKILL.md when it is absent. False when it isn't a string.</summary>
        private static bool TryPath(JsonElement args, out string? relative)
        {
            relative = SkillLibrary.SkillFile;
            if (!args.TryGetProperty("path", out var el) || el.ValueKind == JsonValueKind.Null)
                return true;
            relative = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
            return relative is not null;
        }
    }
}
