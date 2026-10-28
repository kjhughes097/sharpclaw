using System.Globalization;
using System.Text.Json;
using SharpClaw.Agent.Tools.Workspace;

namespace SharpClaw.Agent.Tools.Utility;

/// <summary>
/// Built-in tools that touch neither files nor the network (agent tools spec section 5).
/// </summary>
public static class UtilityTools
{
    public static IAgentTool[] All(TimeProvider time) => new IAgentTool[] { new CurrentTimeTool(time) };

    public static IReadOnlyCollection<string> Names { get; } = new[] { "current_time" };

    /// <summary>The current date and time, optionally in a named IANA time zone.</summary>
    private sealed class CurrentTimeTool(TimeProvider time) : IAgentTool
    {
        private const int MaxTimezoneLength = 100;

        public string Name => "current_time";
        public string Description => "Get the current date and time (UTC, and optionally in an IANA time zone such as Europe/London).";
        public string ParametersJson => """
            {"type":"object","properties":{"timezone":{"type":"string","description":"Optional IANA time zone id, e.g. Europe/London; omit for UTC"}}}
            """;
        public bool ReturnsUntrustedContent => false;

        public ToolAssessment Assess(JsonElement args)
        {
            if (args.TryGetProperty("timezone", out var tz) && tz.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return new ToolAssessment.Blocked("timezone must be a string");
            if (Args.String(args, "timezone") is { Length: > MaxTimezoneLength })
                return new ToolAssessment.Blocked($"timezone is longer than {MaxTimezoneLength} characters");
            return new ToolAssessment.Scored(RiskLevel.Low, "reads the clock");
        }

        public string Summarize(JsonElement args) => "current time";

        public Task<ToolResult> ExecuteUncheckedAsync(JsonElement args, CancellationToken ct)
        {
            var now = time.GetUtcNow();
            var utc = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

            var id = Args.String(args, "timezone");
            if (string.IsNullOrWhiteSpace(id))
                return Task.FromResult(ToolResult.Success(new { utc, local = utc, timezone = "UTC", dayOfWeek = now.UtcDateTime.DayOfWeek.ToString() }));

            TimeZoneInfo zone;
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return Task.FromResult(ToolResult.Error("invalid_timezone", "Unknown time zone; use an IANA id such as Europe/London."));
            }

            var local = TimeZoneInfo.ConvertTime(now, zone);
            return Task.FromResult(ToolResult.Success(new
            {
                utc,
                local = local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                timezone = zone.Id,
                dayOfWeek = local.DayOfWeek.ToString(),
            }));
        }
    }
}
