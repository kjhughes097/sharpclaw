namespace SharpClaw.Core;

/// <summary>
/// Represents an agent definition as stored in the database, with each
/// property held in its own column rather than as raw markdown content.
/// </summary>
public sealed record AgentRecord(
    string Slug,
    string Name,
    string Description,
    string Backend,
    string Model,
    IReadOnlyList<string> McpServers,
    IReadOnlyDictionary<string, string> PermissionPolicy,
    string SystemPrompt,
    bool IsEnabled)
{
    /// <summary>
    /// Converts this database record to an <see cref="AgentPersona"/> ready for
    /// use at runtime, parsing the raw permission strings into <see cref="ToolPermission"/> values.
    /// </summary>
    public AgentPersona ToPersona()
    {
        var policy = new Dictionary<string, ToolPermission>();
        foreach (var (pattern, raw) in PermissionPolicy)
        {
            var normalized = raw.Replace("_", "");
            if (Enum.TryParse<ToolPermission>(normalized, ignoreCase: true, out var perm))
                policy[pattern] = perm;
        }

        return new AgentPersona(Name, Description, SystemPrompt, McpServers, policy, Backend, Model, IsEnabled);
    }
}
