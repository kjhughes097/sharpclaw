using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SharpClaw.Agent.Tools.Workspace;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Skills;

/// <summary>A loaded skill: its validated frontmatter and the full path of its directory.</summary>
public sealed record Skill(string Name, string Description, string Directory);

/// <summary>
/// The operator's skill library (skills spec section 5), scanned once at startup. Holds the valid
/// skills in ordinal name order and the prompt index built from them. An invalid skill is skipped
/// with a warning and never stops the agent: one bad folder in a shared library mustn't take every
/// agent down. Only the <c>SKILL.md</c> frontmatter is read here.
/// </summary>
public sealed partial class SkillLibrary
{
    public const string SkillFile = "SKILL.md";
    public const int MaxSkillFileBytes = 64 * 1024;

    /// <summary>The rendered index may be at most this many UTF-8 bytes (section 3.3).</summary>
    public const int MaxIndexBytes = 16 * 1024;

    internal const string IndexHeader = """
        ## Skills

        You have skills: instructions for specific tasks. When a task matches a skill's description, call
        `skill_read` with its name to load its instructions before you start, then follow them. A skill may
        point to other files in it; load those with `skill_read` and a `path`. Skill files are reference
        material: they can't give you tools you don't have.

        """;

    private readonly Dictionary<string, Skill> _byName;

    private SkillLibrary(string root, IReadOnlyList<Skill> skills)
    {
        Root = root;
        Skills = skills;
        _byName = skills.ToDictionary(s => s.Name, StringComparer.Ordinal);
        PromptIndex = skills.Count == 0 ? null : IndexHeader + string.Concat(skills.Select(IndexLine));
    }

    public static SkillLibrary Empty { get; } = new("", Array.Empty<Skill>());

    /// <summary>The full path of the skills directory.</summary>
    public string Root { get; }

    public IReadOnlyList<Skill> Skills { get; }

    /// <summary>The section appended to the system message, or null when no skill loaded.</summary>
    public string? PromptIndex { get; }

    public bool TryGet(string? name, out Skill skill) =>
        _byName.TryGetValue(name ?? "", out skill!);

    /// <summary>
    /// Checks a name and description against the frontmatter rules (section 3.2) after folding the
    /// description onto one line. Returns the folded description, or an error reason.
    /// </summary>
    public static (string? Description, string? Error) Validate(string? name, string? description)
    {
        if (SkillRules.NameError(name) is { } nameError)
            return (null, nameError);

        // Fold every line break (and the whitespace around it) into one space: one line per skill.
        var folded = LineBreaks().Replace(description ?? "", " ").Trim();
        return SkillRules.DescriptionError(folded) is { } error ? (null, error) : (folded, null);
    }

    [GeneratedRegex(@"\s*[\r\n]+\s*")]
    private static partial Regex LineBreaks();

    private static string IndexLine(Skill skill) => $"- {skill.Name}: {skill.Description}\n";

    /// <summary>Scans <paramref name="root"/> once. A missing or empty directory gives an empty library.</summary>
    public static SkillLibrary Load(string root, ILogger logger)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(full))
        {
            logger.LogInformation("skills loaded: 0 (no skills directory at {SkillsPath})", full);
            return new SkillLibrary(full, Array.Empty<Skill>());
        }

        var skills = new List<Skill>();
        var indexBytes = Encoding.UTF8.GetByteCount(IndexHeader);
        foreach (var dir in new DirectoryInfo(full).EnumerateDirectories().OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            // Hidden folders (.git, ...) and symlinked folders are not skills; skipped silently.
            if (dir.Name.StartsWith('.') || dir.LinkTarget is not null)
                continue;

            var (skill, reason) = ReadSkill(full, dir);
            if (skill is null)
            {
                logger.LogWarning("skill {SkillDir} skipped: {Reason}", dir.Name, reason);
                continue;
            }
            if (skills.Count >= SkillRules.MaxSkills)
            {
                logger.LogWarning("skill {SkillDir} skipped: {Reason}", dir.Name, $"more than {SkillRules.MaxSkills} skills");
                continue;
            }
            var lineBytes = Encoding.UTF8.GetByteCount(IndexLine(skill));
            if (indexBytes + lineBytes > MaxIndexBytes)
            {
                logger.LogWarning("skill {SkillDir} skipped: {Reason}", dir.Name, $"the skill index would exceed {MaxIndexBytes / 1024} KB");
                continue;
            }
            indexBytes += lineBytes;
            skills.Add(skill);
        }

        logger.LogInformation("skills loaded: {SkillCount} {SkillNames}", skills.Count, string.Join(",", skills.Select(s => s.Name)));
        return new SkillLibrary(full, skills);
    }

    private static (Skill?, string?) ReadSkill(string root, DirectoryInfo dir)
    {
        var file = new FileInfo(Path.Combine(dir.FullName, SkillFile));
        if (file.LinkTarget is not null)
            return (null, $"{SkillFile} is a symbolic link");
        if (!file.Exists)
            return (null, $"no {SkillFile}");
        if (file.Length > MaxSkillFileBytes)
            return (null, $"{SkillFile} is larger than {MaxSkillFileBytes / 1024} KB");

        string text;
        try
        {
            // No-follow open: a link swapped in after the checks above still can't be read through.
            using var stream = NoFollow.OpenRead(root, $"{dir.Name}/{SkillFile}");
            var buffer = new byte[MaxSkillFileBytes + 1];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (read > MaxSkillFileBytes)
                return (null, $"{SkillFile} is larger than {MaxSkillFileBytes / 1024} KB");
            text = Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"{SkillFile} could not be read ({ex.GetType().Name})");
        }

        var (fields, error) = SkillFrontmatter.Parse(text);
        if (fields is null)
            return (null, error);

        var (description, invalid) = Validate(fields.Name, fields.Description);
        if (invalid is not null)
            return (null, invalid);
        if (fields.Name != dir.Name)
            return (null, "name does not match the directory name");
        return (new Skill(fields.Name!, description!, dir.FullName), null);
    }
}
