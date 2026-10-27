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

/// <summary>Browser authentication: users, sessions, CSRF and login hardening (pre-3.3 spec section 4).</summary>
public class AuthTests
{
    private const string Password = "correct horse battery staple";

    private static async Task<GatewayFixture> StartAsync(IDictionary<string, string?>? config = null, ConcurrentQueue<string>? logs = null) =>
        await GatewayFixture.CreateAsync(extraConfig: config, configure: logs is null ? null : b =>
            b.Logging.AddProvider(new TelemetryAndAbortTests.CapturingLoggerProvider(logs)));

    private static async Task CreateUserAsync(GatewayFixture fx, string name = "kevin", string password = Password)
    {
        using var admin = fx.Admin();
        (await admin.PostAsJsonAsync("/api/users", new CreateUserRequest(name, password), SharpClawJson.Options)).EnsureSuccessStatusCode();
    }

    /// <summary>A browser-like client: cookie jar, and an Origin header on every request.</summary>
    private sealed class Browser : IDisposable
    {
        public HttpClient Http { get; }
        public CookieContainer Cookies { get; } = new();
        public string? Csrf { get; set; }

        public Browser(GatewayFixture fx, bool sendOrigin = true)
        {
            Http = new HttpClient(new HttpClientHandler { CookieContainer = Cookies }) { BaseAddress = new Uri(fx.ClientBaseUrl) };
            if (sendOrigin)
                Http.DefaultRequestHeaders.Add("Origin", fx.ClientBaseUrl);
        }

        public async Task<HttpResponseMessage> LoginAsync(string user = "kevin", string password = Password)
        {
            var resp = await Http.PostAsJsonAsync("/api/auth/login", new LoginRequest(user, password), SharpClawJson.Options);
            if (resp.IsSuccessStatusCode)
                Csrf = (await resp.Content.ReadFromJsonAsync<LoginResponse>(SharpClawJson.Options))!.CsrfToken;
            return resp;
        }

        public Task<HttpResponseMessage> PostAsync(string path, object body, bool withCsrf = true)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: SharpClawJson.Options) };
            if (withCsrf && Csrf is not null)
                req.Headers.Add("X-CSRF-Token", Csrf);
            return Http.SendAsync(req);
        }

        public void Dispose() => Http.Dispose();
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<ApiErrorResponse>(SharpClawJson.Options))!.Error.Code;

    [Fact]
    public async Task Login_sets_a_strict_http_only_cookie_and_the_session_works()
    {
        await using var fx = await StartAsync();
        await CreateUserAsync(fx);
        using var browser = new Browser(fx);

        var resp = await browser.LoginAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var cookie = Assert.Single(resp.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("sharpclaw_session=", cookie);
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("samesite=strict", cookie.ToLowerInvariant());
        Assert.Contains("path=/", cookie.ToLowerInvariant());
        Assert.DoesNotContain("secure", cookie.ToLowerInvariant());
        Assert.False(string.IsNullOrEmpty(browser.Csrf));

        Assert.Equal(HttpStatusCode.OK, (await browser.Http.GetAsync("/api/agents")).StatusCode);
        var info = await browser.Http.GetFromJsonAsync<SessionInfoResponse>("/api/auth/session", SharpClawJson.Options);
        Assert.True(info!.Authenticated);
        Assert.Equal("session", info.Method);
        Assert.Equal("kevin", info.User!.Name);
        Assert.Equal(browser.Csrf, info.CsrfToken);

        // Without credentials: authenticated false, nothing else.
        using var anon = fx.ClientRaw();
        Assert.Equal("""{"authenticated":false}""", await anon.GetStringAsync("/api/auth/session"));
        using var admin = fx.Admin();
        Assert.Equal("admin_key", (await admin.GetFromJsonAsync<SessionInfoResponse>("/api/auth/session", SharpClawJson.Options))!.Method);
    }

    [Theory]
    [InlineData("Always", null, true)]
    [InlineData("Never", "https", false)]
    [InlineData("Auto", "https", true)]
    [InlineData("Auto", null, false)]
    public async Task Secure_flag_follows_the_setting_and_forwarded_proto(string mode, string? forwardedProto, bool secure)
    {
        await using var fx = await StartAsync(new Dictionary<string, string?>
        {
            ["Auth:Session:CookieSecure"] = mode,
            ["Auth:ForwardedHeaders:Enabled"] = "true",
            ["Auth:ForwardedHeaders:KnownProxies:0"] = "127.0.0.1",
            ["Auth:ForwardedHeaders:KnownProxies:1"] = "::1",
        });
        await CreateUserAsync(fx);
        using var browser = new Browser(fx, sendOrigin: false);
        if (forwardedProto is not null)
        {
            browser.Http.DefaultRequestHeaders.Add("X-Forwarded-Proto", forwardedProto);
            browser.Http.DefaultRequestHeaders.Add("Origin", fx.ClientBaseUrl.Replace("http://", $"{forwardedProto}://"));
        }
        else
        {
            browser.Http.DefaultRequestHeaders.Add("Origin", fx.ClientBaseUrl);
        }

        var resp = await browser.LoginAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(secure, Assert.Single(resp.Headers.GetValues("Set-Cookie")).ToLowerInvariant().Contains("secure"));
    }

    [Fact]
    public async Task Wrong_password_unknown_user_and_disabled_user_look_identical()
    {
        await using var fx = await StartAsync();
        await CreateUserAsync(fx);
        await CreateUserAsync(fx, "disabled");
        using (var admin = fx.Admin())
            (await admin.PatchAsJsonAsync("/api/users/disabled", new UpdateUserRequest(true), SharpClawJson.Options)).EnsureSuccessStatusCode();
        using var browser = new Browser(fx);

        async Task<(string Body, HttpStatusCode Status, TimeSpan Took)> Attempt(string user, string password)
        {
            var sw = Stopwatch.StartNew();
            var r = await browser.LoginAsync(user, password);
            return (await r.Content.ReadAsStringAsync(), r.StatusCode, sw.Elapsed);
        }

        var wrong = await Attempt("kevin", "not the right password");
        var unknown = await Attempt("nobody", "not the right password");
        var disabled = await Attempt("disabled", Password);

        Assert.All(new[] { wrong, unknown, disabled }, a => Assert.Equal(HttpStatusCode.Unauthorized, a.Status));
        Assert.Equal(wrong.Body, unknown.Body);
        Assert.Equal(wrong.Body, disabled.Body);
        Assert.Contains("Invalid username or password.", wrong.Body);
        // The unknown user still pays for a full PBKDF2 computation (the dummy hash).
        Assert.True(unknown.Took > wrong.Took * 0.4, $"unknown {unknown.Took} vs wrong {wrong.Took}");
    }

    [Fact]
    public async Task Failures_are_rate_limited_per_ip_with_retry_after_and_the_window_uses_the_clock()
    {
        await using var fx = await StartAsync(new Dictionary<string, string?>
        {
            ["Auth:Login:MaxFailures"] = "2",
            ["Auth:Login:WindowMinutes"] = "15",
        });
        await CreateUserAsync(fx);
        using var browser = new Browser(fx);

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.LoginAsync("a1", "wrong password!!")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.LoginAsync("a2", "wrong password!!")).StatusCode);
        var limited = await browser.LoginAsync("kevin", Password);

        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.Equal(ErrorCodes.RateLimited, await ErrorCodeAsync(limited));
        Assert.True(limited.Headers.RetryAfter!.Delta > TimeSpan.Zero);

        fx.Time.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(HttpStatusCode.OK, (await browser.LoginAsync()).StatusCode);
    }

    [Fact]
    public async Task Failures_are_rate_limited_per_username_and_success_clears_the_username_counter()
    {
        await using var fx = await StartAsync(new Dictionary<string, string?>
        {
            ["Auth:Login:MaxFailures"] = "2",
            ["Auth:ForwardedHeaders:Enabled"] = "true",
            ["Auth:ForwardedHeaders:KnownProxies:0"] = "127.0.0.1",
            ["Auth:ForwardedHeaders:KnownProxies:1"] = "::1",
        });
        await CreateUserAsync(fx);
        using var browser = new Browser(fx);
        var ip = 0;

        // Each attempt from a different client IP (through the trusted proxy), so only the username counts.
        async Task<HttpResponseMessage> From(string password)
        {
            browser.Http.DefaultRequestHeaders.Remove("X-Forwarded-For");
            browser.Http.DefaultRequestHeaders.Add("X-Forwarded-For", $"203.0.113.{++ip}");
            return await browser.LoginAsync("kevin", password);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await From("wrong password!!")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await From(Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await From("wrong password!!")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await From("wrong password!!")).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await From(Password)).StatusCode);
    }

    [Fact]
    public async Task Cookie_requests_need_origin_and_csrf_but_bearer_and_get_do_not()
    {
        await using var fx = await StartAsync();
        await CreateUserAsync(fx);
        var (agent, _) = await fx.CreateAgentAsync();
        var body = new CreateThreadRequest(agent.Id.ToString(), "t");

        using var noOrigin = new Browser(fx, sendOrigin: false);
        noOrigin.Http.DefaultRequestHeaders.Add("Origin", fx.ClientBaseUrl);
        await noOrigin.LoginAsync();
        noOrigin.Http.DefaultRequestHeaders.Remove("Origin");
        var missing = await noOrigin.PostAsync("/api/threads", body);
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Equal(ErrorCodes.CsrfFailed, await ErrorCodeAsync(missing));

        // Referer is the fallback when Origin is absent.
        noOrigin.Http.DefaultRequestHeaders.Referrer = new Uri(fx.ClientBaseUrl + "/app/chat");
        Assert.Equal(HttpStatusCode.Created, (await noOrigin.PostAsync("/api/threads", body)).StatusCode);

        using var foreign = new Browser(fx, sendOrigin: false);
        foreign.Http.DefaultRequestHeaders.Add("Origin", fx.ClientBaseUrl);
        await foreign.LoginAsync();
        foreign.Http.DefaultRequestHeaders.Remove("Origin");
        foreign.Http.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await foreign.PostAsync("/api/threads", body)).StatusCode);

        using var browser = new Browser(fx);
        await browser.LoginAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostAsync("/api/threads", body, withCsrf: false)).StatusCode);
        var wrongToken = new HttpRequestMessage(HttpMethod.Post, "/api/threads") { Content = JsonContent.Create(body, options: SharpClawJson.Options) };
        wrongToken.Headers.Add("X-CSRF-Token", "not-the-token");
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.Http.SendAsync(wrongToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await browser.PostAsync("/api/threads", body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.Http.GetAsync("/api/threads")).StatusCode);

        // Bearer requests carry no ambient credential: no Origin or CSRF token needed.
        using var admin = fx.Admin();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/threads", body, SharpClawJson.Options)).StatusCode);

        // Login itself checks the origin.
        using var evil = new Browser(fx, sendOrigin: false);
        evil.Http.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await evil.LoginAsync()).StatusCode);
    }

    [Fact]
    public async Task Allowed_origins_extend_the_same_origin_rule()
    {
        await using var fx = await StartAsync(new Dictionary<string, string?> { ["Auth:AllowedOrigins:0"] = "https://claw.example" });
        await CreateUserAsync(fx);
        using var browser = new Browser(fx, sendOrigin: false);
        browser.Http.DefaultRequestHeaders.Add("Origin", "https://claw.example");

        Assert.Equal(HttpStatusCode.OK, (await browser.LoginAsync()).StatusCode);
    }

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
    public async Task Iterations_below_the_minimum_refuse_to_start()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartAsync(new Dictionary<string, string?> { ["Auth:Password:Pbkdf2Iterations"] = "1000" }));
        Assert.Contains("Pbkdf2Iterations", ex.Message);
    }

    [Fact]
    public async Task Logs_never_contain_submitted_usernames_passwords_cookies_or_csrf_tokens()
    {
        var logs = new ConcurrentQueue<string>();
        await using var fx = await StartAsync(logs: logs);
        await CreateUserAsync(fx, "kevin", "TOP-SECRET-PASSWORD");
        using var browser = new Browser(fx);

        await browser.LoginAsync("typo-user-name-xyz", "PASSWORD-IN-WRONG-FIELD");
        await browser.LoginAsync("kevin", "TOP-SECRET-PASSWORD");
        var cookie = browser.Cookies.GetCookies(new Uri(fx.ClientBaseUrl))["sharpclaw_session"]!.Value;
        await browser.Http.GetAsync("/api/agents");
        await browser.PostAsync("/api/auth/logout", new { });

        var all = string.Join("\n", logs);
        Assert.DoesNotContain("typo-user-name-xyz", all);
        Assert.DoesNotContain("PASSWORD-IN-WRONG-FIELD", all);
        Assert.DoesNotContain("TOP-SECRET-PASSWORD", all);
        Assert.DoesNotContain(browser.Csrf!, all);
        Assert.DoesNotContain(cookie, all);
        Assert.DoesNotContain("pbkdf2-sha256", all);
        Assert.Contains("login succeeded", all);
    }
}
