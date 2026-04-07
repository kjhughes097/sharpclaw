using Microsoft.AspNetCore.Mvc;
using SharpClaw.Api.Models;
using SharpClaw.Core;

namespace SharpClaw.Api.Controllers;

[ApiController]
[Route("api/token-usage")]
public sealed class TokenUsageController(SessionStore store) : ControllerBase
{
    [HttpGet("summary")]
    [ProducesResponseType<TokenUsageSummaryDto>(StatusCodes.Status200OK)]
    public IActionResult GetSummary()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var providerUsages = store.GetProviderDailyUsageSummary(today);
        var agentUsages = store.GetAgentDailyUsageSummary(today);

        var providerDtos = providerUsages.Select(p => new ProviderDailyUsageDto(
            p.Provider,
            p.TotalTokens,
            p.DailyLimit,
            p.DailyLimit > 0 ? Math.Round((double)p.TotalTokens / p.DailyLimit * 100, 1) : 0)).ToList();

        var agentDtos = agentUsages.Select(a => new AgentDailyUsageDto(
            a.AgentSlug,
            a.TotalTokens,
            a.DailyLimit,
            a.DailyLimit.HasValue && a.DailyLimit.Value > 0
                ? Math.Round((double)a.TotalTokens / a.DailyLimit.Value * 100, 1)
                : null)).ToList();

        return Ok(new TokenUsageSummaryDto(providerDtos, agentDtos));
    }

    [HttpGet("history")]
    [ProducesResponseType<TokenUsageHistoryDto>(StatusCodes.Status200OK)]
    public IActionResult GetHistory([FromQuery] string period = "week")
    {
        var validPeriods = new[] { "day", "week", "month" };
        if (!validPeriods.Contains(period, StringComparer.OrdinalIgnoreCase))
            period = "week";

        var dataPoints = store.GetTokenUsageHistory(period);
        var dtos = dataPoints.Select(dp => new TokenUsageDataPointDto(dp.Bucket, dp.AgentSlug, dp.TotalTokens)).ToList();

        return Ok(new TokenUsageHistoryDto(period, dtos));
    }
}
