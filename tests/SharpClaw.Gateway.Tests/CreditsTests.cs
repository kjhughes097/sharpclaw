using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Tests;

/// <summary>The cached OpenRouter credits balance (3.3.1 spec section 3.4).</summary>
public class CreditsTests
{
    private const string ManagementKey = "mgmt-SECRET-key";
    private const string Body = """{"data":{"total_credits":20.0,"total_usage":16.64}}""";

    /// <summary>Answers each request with the current <see cref="Respond"/> and records the auth header.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Respond { get; set; } = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        public ConcurrentQueue<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Enqueue($"{request.RequestUri} {request.Headers.Authorization}");
            return Task.FromResult(Respond());
        }
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent(Body) };

    private static Task<GatewayFixture> StartRealAsync(
        ScriptedHandler handler, string managementKey = ManagementKey, ConcurrentQueue<string>? logs = null) =>
        GatewayFixture.CreateAsync(
            extraConfig: new Dictionary<string, string?>
            {
                ["Provider:Type"] = "OpenRouter",
                ["OpenRouter:ApiKey"] = "chat-key",
                ["OpenRouter:ManagementKey"] = managementKey,
                ["OpenRouter:BaseUrl"] = "http://credits.test/api/v1",
            },
            configure: b =>
            {
                b.Services.AddHttpClient(CreditsRefresher.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
                if (logs is not null)
                    b.Logging.AddProvider(new TelemetryAndAbortTests.CapturingLoggerProvider(logs));
            });

    private static async Task<CreditsResponse> GetAsync(GatewayFixture fx)
    {
        using var admin = fx.Admin();
        return (await admin.GetFromJsonAsync<CreditsResponse>("/api/credits", SharpClawJson.Options))!;
    }

    [Fact]
    public async Task A_successful_fetch_reports_the_balance_without_logging_the_key_or_body()
    {
        var handler = new ScriptedHandler { Respond = Ok };
        var logs = new ConcurrentQueue<string>();
        await using var fx = await StartRealAsync(handler, logs: logs);

        await TestHelpers.EventuallyAsync(async () => (await GetAsync(fx)).Status == CreditsStatuses.Ok);
        var credits = await GetAsync(fx);
        Assert.Equal(3.36, credits.RemainingUsd!.Value, 6);
        Assert.Equal((20.0, 16.64, 5.0), (credits.TotalCreditsUsd, credits.TotalUsageUsd, credits.LowThresholdUsd));
        Assert.Equal(fx.Time.GetUtcNow().UtcDateTime, credits.AsOf);
        Assert.Equal($"http://credits.test/api/v1/credits Bearer {ManagementKey}", Assert.Single(handler.Requests));

        Assert.DoesNotContain(logs, l => l.Contains(ManagementKey) || l.Contains("16.64"));
    }

    [Fact]
    public async Task A_failure_reports_failing_and_retries_after_an_hour()
    {
        var handler = new ScriptedHandler();
        await using var fx = await StartRealAsync(handler);

        await TestHelpers.EventuallyAsync(async () => (await GetAsync(fx)).Status == CreditsStatuses.Failing);
        var failing = await GetAsync(fx);
        Assert.Null(failing.RemainingUsd);
        Assert.Null(failing.AsOf);
        Assert.NotNull(failing.LastAttemptAt);

        handler.Respond = Ok;
        fx.Time.Advance(CreditsRefresher.RetryInterval);
        await TestHelpers.EventuallyAsync(async () => (await GetAsync(fx)).Status == CreditsStatuses.Ok);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task After_a_success_a_failure_keeps_the_last_amounts()
    {
        var handler = new ScriptedHandler { Respond = Ok };
        await using var fx = await StartRealAsync(handler);
        await TestHelpers.EventuallyAsync(async () => (await GetAsync(fx)).Status == CreditsStatuses.Ok);
        var asOf = (await GetAsync(fx)).AsOf;

        handler.Respond = () => throw new HttpRequestException("boom");
        fx.Time.Advance(TimeSpan.FromHours(24));
        await TestHelpers.EventuallyAsync(async () => (await GetAsync(fx)).Status == CreditsStatuses.Failing);
        var failing = await GetAsync(fx);
        Assert.Equal(3.36, failing.RemainingUsd!.Value, 6);
        Assert.Equal(asOf, failing.AsOf);
    }

    [Fact]
    public async Task Without_a_management_key_it_is_not_configured_and_never_fetches()
    {
        var handler = new ScriptedHandler { Respond = Ok };
        await using var fx = await StartRealAsync(handler, managementKey: "");
        Assert.Equal(CreditsStatuses.NotConfigured, (await GetAsync(fx)).Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_fake_provider_is_unsupported()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var credits = await GetAsync(fx);
        Assert.Equal(CreditsStatuses.Unsupported, credits.Status);
        Assert.Null(credits.RemainingUsd);
    }

    [Fact]
    public void A_success_older_than_the_refresh_interval_plus_an_hour_is_stale()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-28T06:00:00Z"));
        var state = new CreditsState(time,
            Options.Create(new ProviderOptions { Type = "OpenRouter" }),
            Options.Create(new OpenRouterOptions { ManagementKey = ManagementKey }),
            Options.Create(new CreditsOptions { RefreshHours = 24 }));
        Assert.Equal(CreditsStatuses.Stale, state.ToResponse().Status); // nothing fetched yet

        state.RecordSuccess(20, 16.64, time.GetUtcNow().UtcDateTime);
        time.Advance(TimeSpan.FromHours(25));
        Assert.Equal(CreditsStatuses.Ok, state.ToResponse().Status);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(CreditsStatuses.Stale, state.ToResponse().Status);
    }

    [Fact]
    public async Task Credits_is_not_served_on_the_agent_listener()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (_, token) = await fx.CreateAgentAsync();
        using var agent = fx.AgentClient(token);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync("/api/credits")).StatusCode);
    }
}
