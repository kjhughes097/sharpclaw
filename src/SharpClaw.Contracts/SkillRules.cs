using System.Text.RegularExpressions;

namespace SharpClaw.Contracts;

/// <summary>
/// Skill name and description rules (skills spec sections 3.2 and 3.3), shared by the agent's
/// loader and the gateway's <c>PUT /agent/skills</c> validation so the two can't drift.
/// </summary>
public static partial class SkillRules
{
    public const int MaxSkills = 100;
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 1024;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex NamePattern();

    /// <summary>Returns why a skill name is invalid, or null when it is valid.</summary>
    public static string? NameError(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "name is missing";
        if (name.Length > MaxNameLength)
            return $"name is longer than {MaxNameLength} characters";
        if (!NamePattern().IsMatch(name))
            return "name must be lowercase letters, digits and single hyphens";
        return null;
    }

    /// <summary>
    /// Returns why a description is invalid, or null when it is valid: 1 to
    /// <see cref="MaxDescriptionLength"/> characters after trimming, on a single line.
    /// </summary>
    public static string? DescriptionError(string? description)
    {
        var trimmed = description?.Trim() ?? "";
        if (trimmed.Length == 0)
            return "description is missing";
        if (trimmed.Length > MaxDescriptionLength)
            return $"description is longer than {MaxDescriptionLength} characters";
        if (trimmed.Contains('\n') || trimmed.Contains('\r'))
            return "description must be a single line";
        return null;
    }
}
