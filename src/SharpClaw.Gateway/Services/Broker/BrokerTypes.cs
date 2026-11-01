using System.Text.Encodings.Web;
using System.Text.Json;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Auth;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Gateway.Services.Broker;

/// <summary>
/// Runs one broker tool: in the gateway (web search, GitHub) or by forwarding to the agent's own
/// sandbox. The broker has already authenticated the agent, checked the tool is enabled for it and
/// enforced any approval; a handler only validates arguments and does the work.
/// </summary>
public interface IBrokerToolHandler
{
    /// <summary>The tool names this handler serves (see <see cref="BrokerTools"/>).</summary>
    IReadOnlyCollection<string> Tools { get; }

    Task<BrokerResult> InvokeAsync(BrokerInvocation call, CancellationToken ct);

    /// <summary>
    /// Runs before the approval check: the hash an approval for this call must carry (the arguments'
    /// hash, unless the approval also covers other data such as a PR's change set), or a refusal.
    /// </summary>
    Task<(string? ApprovalHash, BrokerResult? Refusal)> PrepareAsync(BrokerInvocation call, CancellationToken ct) =>
        Task.FromResult<(string?, BrokerResult?)>((ToolArgsHash.Compute(call.Arguments), null));
}

/// <summary>
/// One broker call. <see cref="Agent"/> is the authenticated caller and <see cref="Config"/> its
/// operator settings: anything agent-specific (such as which sandbox) comes from these, never from
/// the request. <see cref="Tainted"/> is the agent's own taint flag for the turn (used by <c>ask_agent</c>).
/// </summary>
public sealed record BrokerInvocation(
    AuthenticatedAgent Agent,
    BrokerAgentOptions Config,
    string Tool,
    JsonElement Arguments,
    Guid CorrelationId,
    Guid ThreadId,
    string ToolCallId,
    Guid? ApprovalId,
    bool Tainted = false);

/// <summary>
/// What a handler produced. A tool-level outcome (including a tool error such as a 404 page) is a
/// <see cref="Result"/> object returned to the model with HTTP 200; a broker-level refusal or
/// failure is an HTTP error in the single error shape. Target fields feed the audit only.
/// </summary>
public sealed record BrokerResult(
    int HttpStatus,
    JsonElement? Result,
    string? ErrorCode,
    string? ErrorMessage,
    string? TargetHost = null,
    string? Target = null)
{
    /// <summary>A tool result object, e.g. <c>{ "ok": true, ... }</c> or <c>{ "ok": false, "error": ... }</c>.</summary>
    public static BrokerResult Of(JsonElement result, string? targetHost = null, string? target = null) =>
        new(200, result, null, null, targetHost, target);

    /// <summary>
    /// Results go to the agent as JSON and never into HTML, so text is written as UTF-8 and only
    /// JSON's own characters are escaped (the default encoder turns <c>+</c> or <c>é</c> into six bytes).
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(SharpClawJson.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static BrokerResult Ok(object payload, string? targetHost = null, string? target = null)
    {
        var element = JsonSerializer.SerializeToElement(payload, Json);
        var fields = new Dictionary<string, object?> { ["ok"] = true };
        foreach (var p in element.EnumerateObject())
            fields[p.Name] = p.Value;
        return Of(JsonSerializer.SerializeToElement(fields, Json), targetHost, target);
    }

    /// <summary>The tool ran but failed in a way the model should see (bad URL, not found…).</summary>
    public static BrokerResult ToolError(string code, string message, string? targetHost = null, string? target = null) =>
        Of(JsonSerializer.SerializeToElement(new { ok = false, error = code, message }, Json), targetHost, target);

    public static BrokerResult Refused(int status, string code, string message, string? targetHost = null, string? target = null) =>
        new(status, null, code, message, targetHost, target);

    public static BrokerResult Unavailable(string message) =>
        Refused(503, ErrorCodes.ToolUnavailable, message);
}
