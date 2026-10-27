using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway;

public static partial class GatewayServices
{
    static partial void RegisterServices(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IThreadEventBus, ThreadEventBus>();
        builder.Services.AddSingleton<IEventPublisher, EventPublisher>();
        builder.Services.AddSingleton<ActiveTurnBuffers>();
        builder.Services.AddSingleton<EventRetentionService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<EventRetentionService>());
        builder.Services.AddSingleton<IAgentInboxSignal, AgentInboxSignal>();
        builder.Services.AddSingleton<ApprovalSignal>();
        builder.Services.AddSingleton<ApprovalDecisions>();
        builder.Services.TryAddSingleton<IApprovalNotifier, NoApprovalNotifier>();
        builder.Services.AddSingleton<ApprovalExpirySweeper>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ApprovalExpirySweeper>());
    }
}
