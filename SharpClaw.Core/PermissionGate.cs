using System.Text.RegularExpressions;

namespace SharpClaw.Core;

/// <summary>
/// Evaluates the persona's permission policy before a tool call proceeds.
/// Patterns in the policy are matched as globs (supports * and ? wildcards).
/// Tools not matching any pattern are denied by default.
/// </summary>
public sealed class PermissionGate
{
    private readonly IReadOnlyList<(Regex Pattern, ToolPermission Permission)> _rules;

    public PermissionGate(IReadOnlyDictionary<string, ToolPermission> policy)
    {
        // Pre-compile patterns once. Evaluated in insertion order; first match wins.
        _rules = policy.Select(kvp =>
        (
            new Regex(
                "^" + Regex.Escape(kvp.Key).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant),
            kvp.Value
        )).ToList();
    }

    /// <summary>
    /// Evaluates the policy for the given tool name and arguments.
    /// Returns <c>true</c> if the call should proceed, <c>false</c> to block it.
    /// For <see cref="ToolPermission.Ask"/>, prompts the user on the console.
    /// </summary>
    public bool Evaluate(string toolName, IReadOnlyDictionary<string, object?>? arguments)
    {
        var permission = Resolve(toolName);

        return permission switch
        {
            ToolPermission.AutoApprove => true,
            ToolPermission.Deny => Denied(toolName),
            ToolPermission.Ask => Prompt(toolName, arguments),
            _ => Denied(toolName),
        };
    }

    /// <summary>
    /// Returns the resolved <see cref="ToolPermission"/> for the given tool name
    /// without executing the action (no console prompt, no deny side-effects).
    /// </summary>
    public ToolPermission Resolve(string toolName)
    {
        // Iterate rules in priority order (first match wins), trying all
        // candidate forms of the tool name against each rule. This ensures
        // specific rules like 'filesystem.write_*' match before a '*' catch-all.
        var candidates = GetCandidates(toolName).ToList();
        foreach (var (pattern, permission) in _rules)
        {
            foreach (var candidate in candidates)
            {
                if (pattern.IsMatch(candidate))
                    return permission;
            }
        }

        // Default-deny anything not in the policy.
        return ToolPermission.Deny;
    }

    private static IEnumerable<string> GetCandidates(string toolName)
    {
        yield return toolName;

        // Tool names use '-' as the namespace separator (e.g. filesystem-write_file)
        // but permission policies use '.' (e.g. filesystem.write_*). Yield a
        // dot-normalized form so patterns match correctly.
        var hyphenIndex = toolName.IndexOf('-');
        if (hyphenIndex > 0 && hyphenIndex < toolName.Length - 1)
        {
            var dotNormalized = string.Concat(toolName.AsSpan(0, hyphenIndex), ".", toolName.AsSpan(hyphenIndex + 1));
            yield return dotNormalized;
            yield return toolName[(hyphenIndex + 1)..];
        }

        var dotIndex = toolName.IndexOf('.');
        if (dotIndex > 0 && dotIndex < toolName.Length - 1)
            yield return toolName[(dotIndex + 1)..];
    }

    private static bool Denied(string toolName)
    {
        Console.Error.WriteLine($"⛔ Tool '{toolName}' blocked by permission policy.");
        return false;
    }

    private static bool Prompt(string toolName, IReadOnlyDictionary<string, object?>? arguments)
    {
        var argSummary = arguments is { Count: > 0 }
            ? string.Join(", ", arguments.Select(kvp => $"{kvp.Key}={kvp.Value}"))
            : "(no arguments)";

        Console.Write($"Allow {toolName} ({argSummary})? [y/N] ");
        var input = Console.ReadLine();
        return string.Equals(input?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
    }
}
