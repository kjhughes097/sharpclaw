---
sidebar_position: 6
---

# Commands

Commands are dot-prefixed shortcuts that bypass the LLM entirely. They're processed by `CommandRouter` before any agent invocation.

## Built-in Commands

| Command          | Class                | Description                               |
| ---------------- | -------------------- | ----------------------------------------- |
| `.ping` / `hi`   | `PingCommand`        | Shows agent identity card (markdown table) |
| `.switch {name}` | `SwitchAgentCommand` | Switches the session to a different agent |
| `.reload`        | `ReloadCommand`      | Reloads agents, tools, and MCP configs    |
| `.new`           | `NewSessionCommand`  | Starts a fresh session for the current agent |
| `.restart`       | `RestartCommand`     | Builds and restarts SharpClaw             |
| `.restart <svc>` | `RestartCommand`     | Restarts a managed service                |
| `.restart all`   | `RestartCommand`     | Restarts SharpClaw and all managed services |
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

## Restart Command

The `.restart` command allows rebuilding and restarting SharpClaw (or managed services) from within a conversation.

### Behaviour

1. **In-flight check** — If any agent sessions have active LLM conversations, the command warns the user and requires `.restart --force` to proceed.
2. **Build validation** — Runs `dotnet build` on the SharpClaw project. If the build fails, the restart is aborted and the error output is shown.
3. **Signal file** — On successful build, writes `.sharpclaw.restart` to the repo root. The watcher process (started by `sharpclaw.sh`) detects this, stops the running process, and starts a fresh one.
4. **Managed services** — `.restart <service>` directly stops and restarts a named service via `ServiceRunner` (no signal file needed).

### Syntax

| Command | Description |
| ------- | ----------- |
| `.restart` | Build and restart SharpClaw |
| `.restart --force` | Skip in-flight session warning |
| `.restart <name>` | Restart a specific managed service |
| `.restart all` | Restart SharpClaw and all running managed services |

## Adding a Command

1. Implement `ICommand` in `src/SharpClaw/Commands/`
2. Register in DI: `builder.Services.AddSingleton<ICommand, MyCommand>()`
