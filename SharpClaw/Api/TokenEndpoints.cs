using SharpClaw.Auditing;

namespace SharpClaw.Api;

public static class TokenEndpoints
{
    public static void MapTokenEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/tokens");

        group.MapGet("/summary", (
            TokenUsageService service,
            string? from,
            string? to,
            string? agent,
            string? provider) =>
        {
            var fromDate = from is not null ? DateTimeOffset.Parse(from) : (DateTimeOffset?)null;
            var toDate = to is not null ? DateTimeOffset.Parse(to) : (DateTimeOffset?)null;
            return Results.Ok(service.GetSummary(fromDate, toDate, agent, provider));
        });

        group.MapGet("/daily", (
            TokenUsageService service,
            string? from,
            string? to,
            string? agent,
            string? provider) =>
        {
            var fromDate = from is not null ? DateTimeOffset.Parse(from) : (DateTimeOffset?)null;
            var toDate = to is not null ? DateTimeOffset.Parse(to) : (DateTimeOffset?)null;
            return Results.Ok(service.GetDaily(fromDate, toDate, agent, provider));
        });

        group.MapGet("/recent", (
            TokenUsageService service,
            int? limit,
            string? agent,
            string? provider) =>
        {
            return Results.Ok(service.GetRecent(limit ?? 50, agent, provider));
        });
    }
}
