using System.Text.Json;
using SharpClaw.Contracts;

namespace SharpClaw.Gateway.Services.Broker;

/// <summary>Sandbox tools that need nothing from the gateway beyond forwarding to the agent's own runner.</summary>
public sealed class SandboxToolHandler(SandboxClient sandbox) : IBrokerToolHandler
{
    public IReadOnlyCollection<string> Tools { get; } = [BrokerTools.HttpFetch, BrokerTools.DownloadToWorkspace, BrokerTools.ExtractText];

    public Task<BrokerResult> InvokeAsync(BrokerInvocation call, CancellationToken ct) =>
        sandbox.CallAsync(call.Config, call.Tool, call.Arguments, TimeoutFor(call.Tool), TargetHost(call.Arguments), ct);

    /// <summary>The host a network tool reaches, for the audit only (the runner and proxy enforce the rules).</summary>
    internal static string? TargetHost(JsonElement args) =>
        args.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String &&
        ToolUrls.Parse(url.GetString()).Url is { } parsed
            ? parsed.Host
            : null;

    /// <summary>A little longer than the runner's own limit for the operation, so its timeout answers first.</summary>
    private static TimeSpan TimeoutFor(string tool) => tool switch
    {
        BrokerTools.HttpFetch or BrokerTools.ExtractText => TimeSpan.FromSeconds(90),
        _ => TimeSpan.FromMinutes(11),
    };
}
