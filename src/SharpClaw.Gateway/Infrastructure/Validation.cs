using System.Text.RegularExpressions;

namespace SharpClaw.Gateway.Infrastructure;

public static partial class Validation
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,31}$")]
    private static partial Regex AgentNameRegex();

    public static bool IsValidAgentName(string name) => AgentNameRegex().IsMatch(name);
}
