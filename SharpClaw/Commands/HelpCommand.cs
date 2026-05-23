namespace SharpClaw.Commands;

public sealed class HelpCommand : ICommand
{
    public bool CanHandle(string text) =>
        text.Trim().Equals(".help", StringComparison.OrdinalIgnoreCase);

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        var help = """
            Available commands:
            .{letter} — Switch to agent starting with that letter
            hi / ping — Show current agent name and description
            .reload — Reload all registries from disk
            .restart — Build and restart SharpClaw (use --force to skip in-flight check)
            .restart <service> — Restart a managed service
            .restart all — Restart SharpClaw and all managed services
            .lsa — List all agents
            .lsm — List all MCP servers
            .lst — List all tools
            .lsmt — List MCP tools for current agent
            .lss — List all skills
            .schedules — List scheduled tasks
            .cron — List cron jobs (with expressions)
            .projects — List all projects
            .tickets — List all tickets across projects
            .tickets <project-id> — List tickets in a project
            .tickets <project-id> <status> — Filter by status
            .help — Show this help message
            """;
        return Task.FromResult(new CommandResult(true, help));
    }
}
