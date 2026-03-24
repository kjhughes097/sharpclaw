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

    private ToolPermission Resolve(string toolName)
    {
        foreach (var candidate in GetCandidates(toolName))
        {
            foreach (var (pattern, permission) in _rules)
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

        var separatorIndex = toolName.IndexOf('.');
        if (separatorIndex <= 0 || separatorIndex >= toolName.Length - 1)
            yield break;

        yield return toolName[(separatorIndex + 1)..];
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
