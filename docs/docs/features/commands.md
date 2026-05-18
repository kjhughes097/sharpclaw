---
sidebar_position: 6
---

# Commands

Commands are dot-prefixed shortcuts that bypass the LLM entirely. They're processed by `CommandRouter` before any agent invocation.

## Built-in Commands

| Command          | Class                | Description                               |
| ---------------- | -------------------- | ----------------------------------------- |
| `.ping`          | `PingCommand`        | Returns "pong" — used for health checks   |
| `.switch {name}` | `SwitchAgentCommand` | Switches the session to a different agent |
| `.reload`        | `ReloadCommand`      | Reloads agents, tools, and MCP configs    |
| `.lsa`           | `ListAgentsCommand`  | Lists all registered agents               |
| `.lsm`           | `ListMcpsCommand`    | Lists all registered MCP servers          |
| `.lst`           | `ListToolsCommand`   | Lists all registered tools                |
| `.lsmt`          | `ListMcpToolsCommand`| Lists discovered MCP tools for current agent |
| `.lss`           | `ListSkillsCommand`  | Lists all registered skills               |
| `.help`          | `HelpCommand`        | Lists available commands                  |

## Command Interface

```csharp
public interface ICommand
{
    bool CanHandle(string rawText);
    Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default);
}
```

## CommandRouter

All `ICommand` implementations are injected via DI. The router iterates through them and executes the first match:

```csharp
public sealed class CommandRouter(IEnumerable<ICommand> commands)
{
    public async Task<CommandResult?> TryExecuteAsync(CommandContext context, CancellationToken ct)
    {
        foreach (var command in _commands)
        {
            if (command.CanHandle(context.RawText))
                return await command.ExecuteAsync(context, ct);
        }
        return null;
    }
}
```

## CommandResult

```csharp
public record CommandResult(string? ResponseText, string? SwitchedToAgent);
```

If `SwitchedToAgent` is set, the session's active agent is updated. If `ResponseText` is set, it's sent back to the user without calling the LLM.

## Adding a Command

1. Implement `ICommand` in `src/SharpClaw/Commands/`
2. Register in DI: `builder.Services.AddSingleton<ICommand, MyCommand>()`
