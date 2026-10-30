using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Gateway.Services.Broker;

/// <summary>
/// <c>sandbox_run</c> (external tools spec section 7.5): <c>bash -c &lt;command&gt;</c> in the agent's
/// sandbox. The broker has already enforced the approval (always required unless the operator
/// removed it from <c>ToolBroker:AlwaysApprove</c>); this handler applies the operator's time limit
/// and forwards only the three known arguments.
/// </summary>
public sealed class SandboxRunToolHandler(SandboxClient sandbox, IOptions<ToolBrokerOptions> options) : IBrokerToolHandler
{
    public const int MaxCommandChars = 64 * 1024;

    public IReadOnlyCollection<string> Tools { get; } = [BrokerTools.SandboxRun];

    public Task<BrokerResult> InvokeAsync(BrokerInvocation call, CancellationToken ct)
    {
        var args = call.Arguments;
        var max = options.Value.SandboxRunMaxSeconds;
        var command = ApprovalDetails.Str(args, "command");
        if (command is not { Length: > 0 and <= MaxCommandChars })
            return Task.FromResult(BrokerResult.ToolError(ErrorCodes.InvalidArguments, $"command must be 1-{MaxCommandChars} characters."));
        var cwd = args.TryGetProperty("cwd", out var c) && c.ValueKind != JsonValueKind.Null ? ApprovalDetails.Str(args, "cwd") : ".";
        if (cwd is null)
            return Task.FromResult(BrokerResult.ToolError(ErrorCodes.InvalidArguments, "cwd must be a workspace folder."));
        var seconds = Math.Min(600, max);
        if (args.TryGetProperty("timeoutSeconds", out var t) && t.ValueKind != JsonValueKind.Null &&
            (t.ValueKind != JsonValueKind.Number || !t.TryGetInt32(out seconds) || seconds < 1 || seconds > max))
            return Task.FromResult(BrokerResult.ToolError(ErrorCodes.InvalidArguments, $"timeoutSeconds must be 1-{max}."));

        var forwarded = JsonSerializer.SerializeToElement(new { command, cwd, timeoutSeconds = seconds });
        return sandbox.CallAsync(call.Config, call.Tool, forwarded, TimeSpan.FromSeconds(seconds + 60), null, ct);
    }
}
