using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpClaw.Gateway.Services;
using SharpClaw.Gateway.Egress;
using SharpClaw.Gateway.Services.Broker;

namespace SharpClaw.Gateway;

public static class ServicesRegistration
{
    public static WebApplicationBuilder AddSharpClawServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IThreadEventBus, ThreadEventBus>();
        builder.Services.AddSingleton<IEventPublisher, EventPublisher>();
        builder.Services.AddSingleton<ActiveTurnBuffers>();
        builder.Services.AddSingleton<EventRetentionService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<EventRetentionService>());
        builder.Services.AddSingleton<IAgentInboxSignal, AgentInboxSignal>();
        builder.Services.AddSingleton<ApprovalSignal>();
        builder.Services.AddSingleton<ApprovalDecisions>();
        builder.Services.AddScoped<MessageTurns>();
        // TryAdd: tests (and later phases) may register their own notifier or summariser first.
        builder.Services.TryAddSingleton<IApprovalNotifier, NoApprovalNotifier>();
        builder.Services.TryAddSingleton<IContextSummariser, NoSummariser>();
        builder.Services.AddSingleton<ApprovalExpirySweeper>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ApprovalExpirySweeper>());
        builder.Services.AddSingleton<CreditsState>();
        builder.Services.AddHttpClient(CreditsRefresher.HttpClientName, c => c.Timeout = CreditsRefresher.RequestTimeout);
        builder.Services.AddHostedService<CreditsRefresher>();
        // Broker tool handlers are added per tool (IBrokerToolHandler); a tool without one is unavailable.
        builder.Services.AddScoped<ToolBroker>();
        // Egress proxy network seams; tests register fakes first.
        builder.Services.TryAddSingleton<IEgressResolver, DnsEgressResolver>();
        builder.Services.TryAddSingleton<IEgressConnector, SocketEgressConnector>();
        return builder;
    }
}
