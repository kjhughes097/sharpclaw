namespace SharpClaw.Gateway.Services;

/// <summary>Which model a thread's LLM calls use (3.3.1 spec section 3.3).</summary>
public static class ModelOverride
{
    /// <summary>
    /// The thread's override while it is still allowed, otherwise the agent's model. <c>Ignored</c>
    /// is true when an override was set but has since left <c>Models:Allowed</c>.
    /// </summary>
    public static (string Model, bool Ignored) Resolve(string? threadModel, string agentModel, IReadOnlyCollection<string> allowed)
    {
        if (threadModel is null)
            return (agentModel, false);
        return allowed.Contains(threadModel, StringComparer.Ordinal) ? (threadModel, false) : (agentModel, true);
    }

    /// <summary>Validates a requested override: null when allowed, otherwise the validation message.</summary>
    public static string? Validate(string model, IReadOnlyCollection<string> allowed) =>
        allowed.Contains(model, StringComparer.Ordinal)
            ? null
            : allowed.Count == 0
                ? "No model overrides are configured (Models:Allowed is empty)."
                : $"model must be one of: {string.Join(", ", allowed)}.";
}
