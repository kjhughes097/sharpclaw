using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Gateway.Services;

/// <summary>
/// Fetches the OpenRouter credits balance at startup, then every <c>Credits:RefreshHours</c>, or
/// hourly while it is failing (3.3.1 spec section 3.4). One attempt per tick, no retries beyond
/// that. Never logs the management key or the response body.
/// </summary>
public sealed class CreditsRefresher(
    IHttpClientFactory httpFactory, CreditsState state, TimeProvider time, IOptions<OpenRouterOptions> openRouter,
    IOptions<CreditsOptions> options, ILogger<CreditsRefresher> logger) : BackgroundService
{
    public const string HttpClientName = "openrouter-credits";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!state.Enabled)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await FetchAsync(stoppingToken);
            // Start the next wait before publishing the result, so anyone who sees the result (a
            // test advancing a fake clock, say) knows the next attempt is already scheduled.
            var wait = Task.Delay(result is null ? RetryInterval : TimeSpan.FromHours(options.Value.RefreshHours),
                time, stoppingToken);
            var now = time.GetUtcNow().UtcDateTime;
            if (result is { } r)
                state.RecordSuccess(r.TotalCredits, r.TotalUsage, now);
            else
                state.RecordFailure(now);
            await wait;
        }
    }

    /// <summary>One fetch of <c>GET /credits</c>; null on any failure.</summary>
    private async Task<(double TotalCredits, double TotalUsage)?> FetchAsync(CancellationToken ct)
    {
        try
        {
            using var http = httpFactory.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{openRouter.Value.BaseUrl.TrimEnd('/')}/credits");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", openRouter.Value.ManagementKey);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("credits fetch failed: status={Status}", (int)resp.StatusCode);
                return null;
            }

            await using var body = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            var data = doc.RootElement.GetProperty("data");
            var credits = data.GetProperty("total_credits").GetDouble();
            var usage = data.GetProperty("total_usage").GetDouble();
            logger.LogInformation("credits fetched");
            return (credits, usage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Timeouts surface as TaskCanceledException without our token being cancelled.
            logger.LogWarning("credits fetch failed: {Error}", ex.GetType().Name);
            return null;
        }
    }
}
