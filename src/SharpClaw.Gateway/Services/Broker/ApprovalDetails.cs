using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Auth;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Gateway.Services.Broker;

public static class ApprovalDetailLimits
{
    /// <summary>The stored detail, whatever the tool.</summary>
    public const int MaxChars = 4000;

    /// <summary>A command shown to the human (Q1).</summary>
    public const int MaxCommandChars = 1000;
}

/// <summary>
/// The gateway's side of an approval for a broker call: it recomputes the arguments hash itself
/// (refusing one that doesn't match what the agent sent) and renders the approval detail the human
/// sees in <c>GET /api/approvals/{id}</c> (Q1). Only the hash and the detail are stored, never the
/// arguments.
/// </summary>
public sealed class ApprovalDetails(PullRequestBuilder pullRequests, IOptions<ToolBrokerOptions> broker)
{
    /// <summary>The stored hash and detail, or a refusal (<c>ErrorStatus</c> and <c>ErrorCode</c>; 400 when null).</summary>
    public sealed record Prepared(string? ArgsHash, string? Detail, string? Error, int? ErrorStatus = null, string? ErrorCode = null);

    public async Task<Prepared> PrepareAsync(
        AuthenticatedAgent agent, string tool, string? argsHash, JsonElement? arguments, CancellationToken ct)
    {
        if (arguments is not { } args || args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return new Prepared(argsHash, null, null);
        if (args.ValueKind != JsonValueKind.Object)
            return new Prepared(null, null, "arguments must be a JSON object.");

        var computed = ToolArgsHash.Compute(args);
        if (argsHash is not null && argsHash != computed)
            return new Prepared(null, null, "argsHash does not match arguments.");

        // A PR's approval also covers its change set: the gateway gathers and checks it now, and
        // anything against the hardline rules is refused here, never offered to the human.
        if (tool == BrokerTools.GitHubCreatePr && broker.Value.ForAgent(agent.Name) is { } config)
        {
            var (plan, refusal) = await pullRequests.PlanAsync(agent, config, args, ct);
            return plan is null ? Refusal(refusal!) : new Prepared(plan.Hash, PullRequestBuilder.Detail(plan), null);
        }
        return new Prepared(computed, Render(tool, args), null);
    }

    /// <summary>A broker refusal, or a tool error (from the sandbox or argument checks), as an approval refusal.</summary>
    private static Prepared Refusal(BrokerResult r)
    {
        if (r.HttpStatus != 200)
            return new Prepared(null, null, r.ErrorMessage, r.HttpStatus, r.ErrorCode);
        var e = r.Result!.Value;
        return new Prepared(null, null, Str(e, "message") ?? "The pull request can't be prepared.", 400, Str(e, "error") ?? ErrorCodes.InvalidArguments);
    }

    /// <summary>The detail text for tools that need one; null for the rest.</summary>
    public static string? Render(string tool, JsonElement args)
    {
        var detail = tool switch
        {
            BrokerTools.SandboxRun => RenderRun(args),
            BrokerTools.GitHubWrite => RenderGitHubWrite(args),
            _ => null,
        };
        return detail is { Length: > ApprovalDetailLimits.MaxChars } ? detail[..ApprovalDetailLimits.MaxChars] : detail;
    }

    private static string? RenderRun(JsonElement args)
    {
        if (Str(args, "command") is not { } command)
            return null;
        var cwd = Str(args, "cwd") is { Length: > 0 } c ? c : ".";
        return $"in {Cap(cwd, 200)}/\n$ {Cap(command, ApprovalDetailLimits.MaxCommandChars)}";
    }

    /// <summary>What will be posted: the target, and the title, labels and text as the human will see them on GitHub.</summary>
    private static string? RenderGitHubWrite(JsonElement args)
    {
        var (request, _) = GitHubRequest.Parse(args, GitHubOperations.Write);
        if (request is null)
            return null;
        var (write, _) = GitHubWrite.Parse(request.Operation, args);
        var lines = new List<string> { $"{request.Operation} {request.Target}" };
        if (write?.Title is { } title)
            lines.Add($"title: {Cap(title, 300)}");
        if (write?.Labels is { Count: > 0 } labels)
            lines.Add($"labels: {string.Join(", ", labels)}");
        if (write?.Body is { } body)
            lines.Add($"---\n{Cap(body, ApprovalDetailLimits.MaxCommandChars)}");
        return string.Join('\n', lines);
    }

    internal static string Cap(string text, int max) =>
        text.Length <= max ? text : $"{text[..max]}… ({text.Length - max} more characters)";

    internal static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
