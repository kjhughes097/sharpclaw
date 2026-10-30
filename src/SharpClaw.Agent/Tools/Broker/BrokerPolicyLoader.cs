using Microsoft.Extensions.Logging;

namespace SharpClaw.Agent.Tools.Broker;

/// <summary>Reads the operator's broker settings at startup, retrying briefly while the gateway starts.</summary>
public static class BrokerPolicyLoader
{
    public static async Task<BrokerPolicy> LoadAsync(IGatewayClient gateway, ILogger logger, CancellationToken ct, int attempts = 5)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var policy = BrokerPolicy.From(await gateway.GetToolPolicyAsync(ct));
                logger.LogInformation("broker policy: always_approve={AlwaysApprove} fetch_allow_hosts={Hosts}",
                    string.Join(",", policy.AlwaysApprove), policy.FetchAllowHosts.Count);
                return policy;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("broker policy unavailable ({Error}); every broker tool will ask for approval", ex.GetType().Name);
                return BrokerPolicy.Conservative;
            }
        }
    }
}
