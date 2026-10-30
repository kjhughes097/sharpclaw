using SharpClaw.Contracts;

namespace SharpClaw.Gateway.Services.Broker;

/// <summary>
/// <c>github_create_pr</c> (spec section 7.7). Scoped to one request: the plan made while preparing
/// (whose change set the approval's hash covers) is exactly the one used to open the PR, so the files
/// can't change between the check and the use.
/// </summary>
public sealed class GitHubCreatePrToolHandler(PullRequestBuilder builder) : IBrokerToolHandler
{
    private PullRequestBuilder.Plan? _plan;

    public IReadOnlyCollection<string> Tools { get; } = [BrokerTools.GitHubCreatePr];

    public async Task<(string? ApprovalHash, BrokerResult? Refusal)> PrepareAsync(BrokerInvocation call, CancellationToken ct)
    {
        var (plan, refusal) = await builder.PlanAsync(call.Agent, call.Config, call.Arguments, ct);
        _plan = plan;
        return (plan?.Hash, refusal);
    }

    public Task<BrokerResult> InvokeAsync(BrokerInvocation call, CancellationToken ct) =>
        _plan is { } plan
            ? builder.OpenAsync(plan, call.Agent.Name, ct)
            : throw new InvalidOperationException("PrepareAsync must run first.");
}
