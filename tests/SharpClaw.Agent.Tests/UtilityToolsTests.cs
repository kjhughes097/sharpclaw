using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using SharpClaw.Agent.Tools;
using SharpClaw.Agent.Tools.Utility;

namespace SharpClaw.Agent.Tests;

public class UtilityToolsTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 4, 13, 30, 0, TimeSpan.Zero));
    private readonly Dictionary<string, IAgentTool> _tools;

    public UtilityToolsTests() => _tools = UtilityTools.All(_time).ToDictionary(t => t.Name);

    private static JsonElement Args(object args) => JsonSerializer.SerializeToElement(args);

    private async Task<JsonElement> Run(string tool, object args)
    {
        var result = await _tools[tool].ExecuteUncheckedAsync(Args(args), CancellationToken.None);
        return JsonDocument.Parse(result.Json).RootElement;
    }

    [Fact]
    public void Names_match_the_tools() =>
        Assert.Equal(UtilityTools.Names.OrderBy(n => n), _tools.Keys.OrderBy(n => n));

    [Fact]
    public void Every_tool_has_a_valid_parameter_schema()
    {
        foreach (var tool in _tools.Values)
        {
            using var schema = JsonDocument.Parse(tool.ParametersJson);
            Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
            Assert.False(tool.ReturnsUntrustedContent);
        }
    }

    [Fact]
    public async Task Current_time_without_a_zone_is_utc()
    {
        var result = await Run("current_time", new { });

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal("2026-07-04T13:30:00Z", result.GetProperty("utc").GetString());
        Assert.Equal("2026-07-04T13:30:00Z", result.GetProperty("local").GetString());
        Assert.Equal("UTC", result.GetProperty("timezone").GetString());
        Assert.Equal("Saturday", result.GetProperty("dayOfWeek").GetString());
    }

    [Fact]
    public async Task Current_time_follows_the_injected_clock()
    {
        _time.Advance(TimeSpan.FromHours(12));
        var result = await Run("current_time", new { });
        Assert.Equal("2026-07-05T01:30:00Z", result.GetProperty("utc").GetString());
        Assert.Equal("Sunday", result.GetProperty("dayOfWeek").GetString());
    }

    [Fact]
    public async Task Current_time_converts_to_a_named_zone()
    {
        // Late evening in UTC is already the next day in Tokyo (UTC+9, no DST).
        _time.Advance(TimeSpan.FromHours(10));
        var result = await Run("current_time", new { timezone = "Asia/Tokyo" });

        Assert.Equal("2026-07-04T23:30:00Z", result.GetProperty("utc").GetString());
        Assert.Equal("2026-07-05T08:30:00+09:00", result.GetProperty("local").GetString());
        Assert.Equal("Asia/Tokyo", result.GetProperty("timezone").GetString());
        Assert.Equal("Sunday", result.GetProperty("dayOfWeek").GetString());
    }

    [Fact]
    public async Task Current_time_rejects_an_unknown_zone()
    {
        var result = await Run("current_time", new { timezone = "Mars/Olympus_Mons" });
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("invalid_timezone", result.GetProperty("error").GetString());
    }

    [Fact]
    public void Current_time_is_low_risk_and_validates_its_argument()
    {
        var tool = _tools["current_time"];
        Assert.Equal(RiskLevel.Low, Assert.IsType<ToolAssessment.Scored>(tool.Assess(Args(new { }))).Level);
        Assert.IsType<ToolAssessment.Scored>(tool.Assess(Args(new { timezone = "Europe/London" })));
        Assert.IsType<ToolAssessment.Blocked>(tool.Assess(Args(new { timezone = 5 })));
        Assert.IsType<ToolAssessment.Blocked>(tool.Assess(Args(new { timezone = new string('x', 101) })));
        Assert.Equal("current time", tool.Summarize(Args(new { timezone = "Europe/London" })));
    }
}
