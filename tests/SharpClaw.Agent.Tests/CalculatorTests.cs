using System.Text.Json;
using SharpClaw.Agent.Tools;
using SharpClaw.Agent.Tools.Utility;

namespace SharpClaw.Agent.Tests;

/// <summary>The calculate tool and its parser (agent tools spec section 5.2).</summary>
public class CalculatorTests
{
    private readonly IAgentTool _tool = UtilityTools.All(TimeProvider.System).Single(t => t.Name == "calculate");

    private static JsonElement Args(object args) => JsonSerializer.SerializeToElement(args);

    private async Task<JsonElement> Run(string expression)
    {
        var result = await _tool.ExecuteUncheckedAsync(Args(new { expression }), CancellationToken.None);
        return JsonDocument.Parse(result.Json).RootElement;
    }

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("10 - 4 - 3", 3)]
    [InlineData("100 / 10 / 5", 2)]
    [InlineData("7 % 4", 3)]
    [InlineData("2 ^ 3 ^ 2", 512)] // right-associative
    [InlineData("2 * 3 ^ 2", 18)]
    [InlineData("-2 ^ 2", -4)]
    [InlineData("2 ^ -1", 0.5)]
    [InlineData("--3", 3)]
    [InlineData("-(4 - 6)", 2)]
    [InlineData("3 - -2", 5)]
    [InlineData(".5 + 1.25", 1.75)]
    [InlineData("abs(-3.5)", 3.5)]
    [InlineData("round(2.5)", 3)]
    [InlineData("round(-2.5)", -3)]
    [InlineData("round(3.14159, 2)", 3.14)]
    [InlineData("floor(-1.5)", -2)]
    [InlineData("CEIL(1.2)", 2)]
    [InlineData("sqrt(16) + 1", 5)]
    [InlineData("min(4, 2, 8)", 2)]
    [InlineData("max(4, -2, 8 / 2 + 1)", 5)]
    [InlineData("  1+\t1  ", 2)]
    public void Evaluates_the_grammar(string expression, double expected) =>
        Assert.Equal(expected, Calculator.Evaluate(expression), 10);

    [Theory]
    [InlineData("1 / 0")]
    [InlineData("5 % 0")]
    [InlineData("1 / (2 - 2)")]
    [InlineData("10 ^ 400")]
    [InlineData("sqrt(-1)")]
    [InlineData("(-8) ^ 0.5")]
    public void Math_errors(string expression) =>
        Assert.Equal(Calculator.MathError, Assert.Throws<CalculatorException>(() => Calculator.Evaluate(expression)).Code);

    [Theory]
    [InlineData("x + 1")]
    [InlineData("pi")]
    [InlineData("exp(1)")]
    [InlineData("System.IO.File(1)")]
    [InlineData("abs 3")]
    [InlineData("1e3")]
    [InlineData("1.2.3")]
    [InlineData(".")]
    [InlineData("1 +")]
    [InlineData("(1 + 2")]
    [InlineData("1 + 2)")]
    [InlineData("")]
    [InlineData("1; 2")]
    [InlineData("1 == 1")]
    [InlineData("'1'")]
    [InlineData("abs(1, 2)")]
    [InlineData("round(1.5, 2.5)")]
    [InlineData("round(1.5, 16)")]
    [InlineData("min()")]
    [InlineData("+1")]
    [InlineData("π")]
    public void Rejects_anything_outside_the_grammar(string expression) =>
        Assert.Equal(Calculator.InvalidExpression, Assert.Throws<CalculatorException>(() => Calculator.Evaluate(expression)).Code);

    [Fact]
    public void Depth_is_limited()
    {
        var ok = new string('(', Calculator.MaxDepth) + "1" + new string(')', Calculator.MaxDepth);
        Assert.Equal(1, Calculator.Evaluate(ok));

        var tooDeep = new string('(', Calculator.MaxDepth + 1) + "1" + new string(')', Calculator.MaxDepth + 1);
        Assert.Equal(Calculator.InvalidExpression, Assert.Throws<CalculatorException>(() => Calculator.Evaluate(tooDeep)).Code);

        var deepCalls = string.Concat(Enumerable.Repeat("abs(", Calculator.MaxDepth + 1)) + "1" + new string(')', Calculator.MaxDepth + 1);
        Assert.Throws<CalculatorException>(() => Calculator.Evaluate(deepCalls));
    }

    [Fact]
    public async Task Tool_returns_the_value_or_an_error_code()
    {
        var ok = await Run("round((12.5 * 3 + 4) / 7, 2)");
        Assert.True(ok.GetProperty("ok").GetBoolean());
        Assert.Equal(5.93, ok.GetProperty("value").GetDouble());
        Assert.Equal("round((12.5 * 3 + 4) / 7, 2)", ok.GetProperty("expression").GetString());

        Assert.Equal("math_error", (await Run("1/0")).GetProperty("error").GetString());
        Assert.Equal("invalid_expression", (await Run("y")).GetProperty("error").GetString());
    }

    [Fact]
    public void Tool_assessment_and_summary()
    {
        Assert.Equal(RiskLevel.Low, Assert.IsType<ToolAssessment.Scored>(_tool.Assess(Args(new { expression = "1+1" }))).Level);
        Assert.IsType<ToolAssessment.Blocked>(_tool.Assess(Args(new { })));
        Assert.IsType<ToolAssessment.Blocked>(_tool.Assess(Args(new { expression = "" })));
        Assert.IsType<ToolAssessment.Blocked>(_tool.Assess(Args(new { expression = new string('1', Calculator.MaxLength + 1) })));

        var summary = _tool.Summarize(Args(new { expression = "123456 * 789" }));
        Assert.Equal("calculate (12 chars)", summary);
        Assert.DoesNotContain("123456", summary);
    }
}
