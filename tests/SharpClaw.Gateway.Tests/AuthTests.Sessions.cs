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

/// <summary>Session lifetime: sliding idle expiry, absolute limit, logout and revocation.</summary>
public partial class AuthTests
{
    [Fact]
    public async Task Sessions_slide_on_idle_and_stop_at_the_absolute_limit()
    {
        await using var fx = await StartAsync(new Dictionary<string, string?>
        {
            ["Auth:Session:IdleTimeoutHours"] = "168",
            ["Auth:Session:AbsoluteTimeoutDays"] = "30",
        });
        await CreateUserAsync(fx);
        using var browser = new Browser(fx);
        await browser.LoginAsync();

        // Used every 6 days: stays alive (sliding) until the 30-day cap.
        for (var day = 6; day < 30; day += 6)
        {
            fx.Time.Advance(TimeSpan.FromDays(6));
            Assert.Equal(HttpStatusCode.OK, (await browser.Http.GetAsync("/api/agents")).StatusCode);
        }
        fx.Time.Advance(TimeSpan.FromDays(6));
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.Http.GetAsync("/api/agents")).StatusCode);

        // Idle for longer than the idle timeout.
        using var idle = new Browser(fx);
        await idle.LoginAsync();
        fx.Time.Advance(TimeSpan.FromHours(169));
        Assert.Equal(HttpStatusCode.Unauthorized, (await idle.Http.GetAsync("/api/agents")).StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_and_password_change_keeps_only_the_current_session()
    {
        await using var fx = await StartAsync();
        await CreateUserAsync(fx);
        using var a = new Browser(fx);
        using var b = new Browser(fx);
        using var c = new Browser(fx);
        await a.LoginAsync();
        await b.LoginAsync();
        await c.LoginAsync();

        var logout = await c.PostAsync("/api/auth/logout", new { });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains("sharpclaw_session=;", Assert.Single(logout.Headers.GetValues("Set-Cookie")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.Http.GetAsync("/api/agents")).StatusCode);

        const string newPassword = "an even better passphrase";
        Assert.Equal(HttpStatusCode.BadRequest,
            (await a.PostAsync("/api/auth/password", new ChangePasswordRequest(Password, "short"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await a.PostAsync("/api/auth/password", new ChangePasswordRequest("wrong current pw", newPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await a.PostAsync("/api/auth/password", new ChangePasswordRequest(Password, newPassword))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await a.Http.GetAsync("/api/agents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.Http.GetAsync("/api/agents")).StatusCode);
        using var d = new Browser(fx);
        Assert.Equal(HttpStatusCode.OK, (await d.LoginAsync("kevin", newPassword)).StatusCode);
    }

    [Fact]
    public async Task Admin_reset_and_disable_revoke_all_sessions()
    {
        await using var fx = await StartAsync();
        await CreateUserAsync(fx);
        using var browser = new Browser(fx);
        await browser.LoginAsync();
        using var admin = fx.Admin();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/users/kevin/password",
            new SetPasswordRequest("brand new passphrase"), SharpClawJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.Http.GetAsync("/api/agents")).StatusCode);

        await browser.LoginAsync("kevin", "brand new passphrase");
        Assert.Equal(HttpStatusCode.OK, (await browser.Http.GetAsync("/api/agents")).StatusCode);
        (await admin.PatchAsJsonAsync("/api/users/kevin", new UpdateUserRequest(true), SharpClawJson.Options)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.Http.GetAsync("/api/agents")).StatusCode);
    }

    [Fact]
    public async Task Cookies_are_ignored_on_the_agent_listener()
    {
        await using var fx = await StartAsync();
        await CreateUserAsync(fx);
        using var browser = new Browser(fx);
        await browser.LoginAsync();
        var cookie = browser.Cookies.GetCookies(new Uri(fx.ClientBaseUrl))["sharpclaw_session"]!.Value;

        using var agentPort = fx.AgentRaw();
        var req = new HttpRequestMessage(HttpMethod.Get, "/agent/inbox?wait=0");
        req.Headers.Add("Cookie", $"sharpclaw_session={cookie}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await agentPort.SendAsync(req)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agentPort.GetAsync("/api/auth/session")).StatusCode);
    }
}
