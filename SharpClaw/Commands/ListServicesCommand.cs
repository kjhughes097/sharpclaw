using SharpClaw.Abstractions;
using SharpClaw.Workers;

namespace SharpClaw.Commands;

public sealed class ListServicesCommand(IServiceRegistry serviceRegistry, ServiceRunner serviceRunner) : ICommand
{
    public bool CanHandle(string text) =>
        text.Trim().Equals(".lss", StringComparison.OrdinalIgnoreCase);

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        var definitions = serviceRegistry.GetAll();
        if (definitions.Count == 0)
            return Task.FromResult(new CommandResult(true, "No services registered."));

        var running = serviceRunner.GetRunningServices();

        var lines = definitions
            .OrderBy(d => d.Name)
            .Select(d =>
            {
                var status = running.TryGetValue(d.Name, out var managed)
                    ? managed.Status.ToString()
                    : "Not started";
                var deps = d.Depends is { Count: > 0 }
                    ? $" → depends: [{string.Join(", ", d.Depends)}]"
                    : "";
                return $"• {d.Name} ({d.Runtime}, port {d.Port}) — {status}{deps}";
            });

        var response = $"Services:\n{string.Join('\n', lines)}";
        return Task.FromResult(new CommandResult(true, response));
    }
}
