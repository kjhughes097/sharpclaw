namespace SharpClaw.Gateway.Auth;

/// <summary>The agent authenticated on the current agent-listener request.</summary>
public sealed record AuthenticatedAgent(Guid Id, string Name, string Model, double DailyBudgetUsd);

public static class AuthContext
{
    private const string Key = "sharpclaw.agent";

    public static void Set(HttpContext ctx, AuthenticatedAgent agent) => ctx.Items[Key] = agent;

    public static AuthenticatedAgent Agent(HttpContext ctx) =>
        (AuthenticatedAgent)(ctx.Items[Key] ?? throw new InvalidOperationException("No authenticated agent on context."));
}
