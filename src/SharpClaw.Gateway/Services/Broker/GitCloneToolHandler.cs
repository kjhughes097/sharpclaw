using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Services.Broker;

/// <summary>
/// <c>git_clone</c> (external tools spec section 7.4). The gateway enforces the operator's host
/// allow-list (public repositories over HTTPS only, no token anywhere), the agent's sandbox runs the
/// fixed clone, and the gateway records where the clone came from and its base commit, from the
/// runner's answer at clone time, for <c>github_create_pr</c>.
/// </summary>
public sealed class GitCloneToolHandler(
    SandboxClient sandbox, SharpClawDbContext db, IOptions<ToolBrokerOptions> options, TimeProvider time) : IBrokerToolHandler
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(11);

    public IReadOnlyCollection<string> Tools { get; } = [BrokerTools.GitClone];

    public async Task<BrokerResult> InvokeAsync(BrokerInvocation call, CancellationToken ct)
    {
        var url = call.Arguments.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        var (host, repo, error) = ToolUrls.ParseGitRepo(url);
        if (host is null)
            return BrokerResult.ToolError(ErrorCodes.InvalidArguments, error!);
        if (!options.Value.CloneHosts.Contains(host))
            return BrokerResult.Refused(403, "host_not_allowed",
                $"Cloning from {host} is not allowed (allowed: {string.Join(", ", options.Value.CloneHosts)}).", host);

        var result = await sandbox.CallAsync(call.Config, call.Tool, call.Arguments, Timeout, host, ct);
        if (result.Result is not { } r || !r.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            return result;

        // The runner reports what it cloned; it must be the repository the gateway allowed.
        var path = Str(r, "path");
        var commit = Str(r, "commit");
        if (path is null || commit is not { Length: 40 or 64 } || !commit.All(char.IsAsciiHexDigitLower) ||
            Str(r, "host") != host || Str(r, "repo") != repo)
            return BrokerResult.Refused(502, ErrorCodes.ToolFailed, "The sandbox gave an unexpected answer.", host);

        await db.SandboxClones.Where(c => c.AgentId == call.Agent.Id && c.Path == path).ExecuteDeleteAsync(ct);
        db.SandboxClones.Add(new SandboxClone
        {
            Id = Guid.CreateVersion7(),
            AgentId = call.Agent.Id,
            Path = path,
            Host = host,
            Repo = repo!,
            BaseSha = commit,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(ct);
        return result with { Target = $"{host}/{repo}" };
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
