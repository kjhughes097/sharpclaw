using System.Text.Json;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Tools;

/// <summary>Ordered risk scale (3.2 spec section 3): Low &lt; Medium &lt; High &lt; Critical.</summary>
public enum RiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3,
}

public static class RiskLevelExtensions
{
    /// <summary>The wire name used in config, approvals and audit (e.g. "high").</summary>
    public static string ToWire(this RiskLevel level) => level switch
    {
        RiskLevel.Low => RiskLevels.Low,
        RiskLevel.Medium => RiskLevels.Medium,
        RiskLevel.High => RiskLevels.High,
        _ => RiskLevels.Critical,
    };

    public static bool TryParse(string? value, out RiskLevel level)
    {
        level = value?.Trim().ToLowerInvariant() switch
        {
            RiskLevels.Low => RiskLevel.Low,
            RiskLevels.Medium => RiskLevel.Medium,
            RiskLevels.High => RiskLevel.High,
            RiskLevels.Critical => RiskLevel.Critical,
            _ => (RiskLevel)(-1),
        };
        return level >= RiskLevel.Low;
    }

    /// <summary>One level higher, capped at Critical (used for taint).</summary>
    public static RiskLevel Raise(this RiskLevel level) =>
        level >= RiskLevel.Critical ? RiskLevel.Critical : level + 1;
}

/// <summary>A tool's own judgement of a call, made from its arguments alone.</summary>
public abstract record ToolAssessment
{
    /// <summary>A confident score.</summary>
    public sealed record Scored(RiskLevel Level, string Reason) : ToolAssessment;

    /// <summary>The rules can't score this call confidently (smart mode may classify it).</summary>
    public sealed record Uncertain(string Reason) : ToolAssessment;

    /// <summary>A hardline rule: never executed, never offered for approval.</summary>
    public sealed record Blocked(string Reason) : ToolAssessment;
}

/// <summary>The JSON string returned to the model as a tool message.</summary>
public sealed record ToolResult(bool Ok, string Json, string? ErrorCode)
{
    public static ToolResult Success(object payload)
    {
        // Merge {"ok":true} with the payload's own properties.
        var element = JsonSerializer.SerializeToElement(payload, SharpClawJson.Options);
        var fields = new Dictionary<string, object?> { ["ok"] = true };
        foreach (var p in element.EnumerateObject())
            fields[p.Name] = p.Value;
        return new ToolResult(true, JsonSerializer.Serialize(fields, SharpClawJson.Options), null);
    }

    public static ToolResult Error(string code, string message) =>
        new(false, JsonSerializer.Serialize(new { ok = false, error = code, message }, SharpClawJson.Options), code);
}

/// <summary>
/// A tool the agent can run. Implementations only describe, assess and execute; every decision
/// about whether a call may run is made by <see cref="ToolDispatcher"/>, which is the only code
/// allowed to call <see cref="ExecuteUncheckedAsync"/> (enforced by an architecture test).
/// </summary>
public interface IAgentTool
{
    string Name { get; }
    string Description { get; }

    /// <summary>JSON Schema for the arguments, passed to the model.</summary>
    string ParametersJson { get; }

    /// <summary>True when results may contain untrusted content (sets taint for the rest of the turn).</summary>
    bool ReturnsUntrustedContent { get; }

    ToolAssessment Assess(JsonElement args);

    /// <summary>Short human-readable description for approval prompts. Never includes content.</summary>
    string Summarize(JsonElement args);

    /// <summary>
    /// What the smart-mode classifier sees instead of <see cref="Summarize"/>: structured, validated
    /// fields only (e.g. tool, host, path), never model-supplied free text (external tools spec
    /// section 8). Null means the summary is used, as for the workspace tools.
    /// </summary>
    IReadOnlyDictionary<string, string>? ClassifierFields(JsonElement args) => null;

    /// <summary>
    /// True for tools the gateway always requires an approval for (<c>ToolBroker:AlwaysApprove</c>):
    /// the dispatcher then asks the human whatever <c>allowedRiskLevel</c> or smart mode says.
    /// </summary>
    bool AlwaysRequiresApproval => false;

    /// <summary>Runs the call without any policy checks. Only <see cref="ToolDispatcher"/> may call this.</summary>
    Task<ToolResult> ExecuteUncheckedAsync(JsonElement args, CancellationToken ct);
}
