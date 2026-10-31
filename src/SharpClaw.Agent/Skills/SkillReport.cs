using Microsoft.Extensions.Logging;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Skills;

/// <summary>
/// Tells the gateway which skills this agent loaded (skills spec section 9.1), so the operator can
/// see them. Sent once at startup; a failure is logged and never stops the agent, and is not retried.
/// </summary>
public static class SkillReport
{
    public static async Task SendAsync(IGatewayClient gateway, SkillLibrary library, ILogger logger, CancellationToken ct)
    {
        var skills = library.Skills.Select(s => new AgentSkillDto(s.Name, s.Description)).ToList();
        try
        {
            await gateway.ReportSkillsAsync(skills, ct);
        }
        catch (HttpRequestException ex)
        {
            // Status code only: the gateway's response body is never logged.
            logger.LogWarning("could not report skills to the gateway: status={StatusCode}",
                ex.StatusCode is { } code ? (int)code : 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("could not report skills to the gateway: {Error}", ex.GetType().Name);
        }
    }
}
