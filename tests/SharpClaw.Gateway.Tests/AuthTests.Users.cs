using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Auth;

namespace SharpClaw.Gateway.Tests;

/// <summary>User administration, password hashing and who decided approvals.</summary>
public partial class AuthTests
{
    [Fact]
    public async Task User_management_rejects_sessions_and_validates_input()
    {
        await using var fx = await StartAsync();
        await CreateUserAsync(fx);
        using var browser = new Browser(fx);
        await browser.LoginAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.Http.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await browser.PostAsync("/api/users", new CreateUserRequest("mallory", "mallory's passphrase"))).StatusCode);

        using var admin = fx.Admin();
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync("/api/users", new CreateUserRequest("kevin", Password), SharpClawJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/users", new CreateUserRequest("Bad Name", Password), SharpClawJson.Options)).StatusCode);
        var tooShort = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest("shorty", "elevenchars"), SharpClawJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.DoesNotContain("elevenchars", await tooShort.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/users",
            new CreateUserRequest("longy", new string('p', 257)), SharpClawJson.Options)).StatusCode);

        var list = await admin.GetStringAsync("/api/users");
        Assert.Contains("kevin", list);
        Assert.DoesNotContain("pbkdf2", list);
        Assert.DoesNotContain("hash", list, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hashes_use_the_documented_format_and_are_upgraded_on_login()
    {
        await using var fx = await StartAsync();
        await CreateUserAsync(fx);
        using (var db = fx.NewDb())
        {
            var user = await db.Users.SingleAsync();
            Assert.Matches(@"^pbkdf2-sha256\$600000\$[A-Za-z0-9+/=]{24}\$[A-Za-z0-9+/=]{44}$", user.PasswordHash);
            user.PasswordHash = PasswordHasher.Hash(Password, 1000);
            await db.SaveChangesAsync();
        }

        using var browser = new Browser(fx);
        Assert.Equal(HttpStatusCode.OK, (await browser.LoginAsync()).StatusCode);

        using var check = fx.NewDb();
        Assert.StartsWith("pbkdf2-sha256$600000$", (await check.Users.SingleAsync()).PasswordHash);
    }

    [Fact]
    public async Task Approval_decisions_record_which_user_or_the_admin_key_decided()
    {
        await using var fx = await StartAsync();
        await CreateUserAsync(fx);
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);

        async Task<Guid> PendingApprovalAsync()
        {
            var corr = await fx.PostUserMessageAsync(thread, "go");
            await fx.ClaimAsync(token);
            return await fx.RequestApprovalAsync(token, thread, corr);
        }

        // A signed-in user decides in the browser.
        var first = await PendingApprovalAsync();
        using var browser = new Browser(fx);
        await browser.LoginAsync();
        (await browser.PostAsync($"/api/approvals/{first}", new DecideApprovalRequest(ApprovalStatuses.Approved)))
            .EnsureSuccessStatusCode();
        using (var c = fx.AgentClient(token))
            (await c.PostAsJsonAsync($"/agent/threads/{thread}/messages",
                new AgentReplyRequest("done", (await fx.NewDb().Approvals.SingleAsync(a => a.Id == first)).CorrelationId),
                SharpClawJson.Options)).EnsureSuccessStatusCode();

        // The CLI decides with the admin key.
        var second = await PendingApprovalAsync();
        using var admin = fx.Admin();
        (await admin.PostAsJsonAsync($"/api/approvals/{second}", new DecideApprovalRequest(ApprovalStatuses.Denied),
            SharpClawJson.Options)).EnsureSuccessStatusCode();

        async Task<ApprovalDetailDto> GetAsync(Guid id) =>
            (await admin.GetFromJsonAsync<ApprovalDetailResponse>($"/api/approvals/{id}", SharpClawJson.Options))!.Approval;
        var byUser = await GetAsync(first);
        Assert.Equal((ApprovalDecidedBy.User, "kevin"), (byUser.DecidedBy, byUser.DecidedByUser));
        var byKey = await GetAsync(second);
        Assert.Equal((ApprovalDecidedBy.User, ApprovalDecidedBy.AdminKeyUser), (byKey.DecidedBy, byKey.DecidedByUser));

        using var db = fx.NewDb();
        var decided = await db.ThreadEvents.AsNoTracking()
            .Where(e => e.ThreadId == thread && e.Type == SseEventNames.ApprovalDecided).OrderBy(e => e.Id).ToListAsync();
        Assert.Contains("\"decidedByUser\":\"kevin\"", decided[0].Data);
        var trace = await admin.GetFromJsonAsync<TraceResponse>($"/api/threads/{thread}/trace", SharpClawJson.Options);
        Assert.Equal("kevin", trace!.Turns[0].Approvals.Single().DecidedByUser);
    }

    [Fact]
    public async Task Iterations_below_the_minimum_refuse_to_start()
    {
        var ex = await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            StartAsync(new Dictionary<string, string?> { ["Auth:Password:Pbkdf2Iterations"] = "1000" }));
        Assert.Contains("Pbkdf2Iterations", ex.Message);
    }
}
