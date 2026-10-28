using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Tests;

/// <summary>Shared helpers for the 3.3 client-API tests.</summary>
public static class ClientApiHelpers
{
    /// <summary>The repository root (where <c>SharpClaw.slnx</c> lives), for snapshot files.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SharpClaw.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>Claims the next message and replies to it.</summary>
    public static async Task ReplyAsync(this GatewayFixture fx, string token, Guid threadId, Guid correlationId, string content)
    {
        var claimed = await fx.ClaimAsync(token);
        Assert.Equal(correlationId, claimed?.Id);
        using var agent = fx.AgentClient(token);
        (await agent.PostAsJsonAsync($"/agent/threads/{threadId}/messages",
            new AgentReplyRequest(content, correlationId), SharpClawJson.Options)).EnsureSuccessStatusCode();
    }

    /// <summary>The agent asks for approval on its claimed message; returns the approval id.</summary>
    public static async Task<Guid> RequestApprovalAsync(
        this GatewayFixture fx, string token, Guid threadId, Guid correlationId, string risk = RiskLevels.High)
    {
        using var agent = fx.AgentClient(token);
        var resp = await agent.PostAsJsonAsync("/agent/approvals", new CreateApprovalRequest(
            correlationId, threadId, "call_1", "workspace_delete", "Delete notes/a.md", risk, "Deletes a file"),
            SharpClawJson.Options);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ApprovalResponse>(SharpClawJson.Options))!.Approval.Id;
    }

    public static async Task<string> ErrorCodeAsync(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<ApiErrorResponse>(SharpClawJson.Options))!.Error.Code;

    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, SharpClawJson.Options)!;
}
