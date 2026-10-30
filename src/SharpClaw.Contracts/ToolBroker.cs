using System.Security.Cryptography;
using System.Text.Json;

namespace SharpClaw.Contracts;

/// <summary>
/// The tools the gateway's broker runs for agents (external tools spec section 7), either in the
/// gateway itself (credentialed calls) or in the agent's own sandbox.
/// </summary>
public static class BrokerTools
{
    public const string HttpFetch = "http_fetch";
    public const string DownloadToWorkspace = "download_to_workspace";
    public const string WebSearch = "web_search";
    public const string ExtractText = "extract_text";
    public const string GitClone = "git_clone";
    public const string SandboxRun = "sandbox_run";
    public const string GitHubRead = "github_read";
    public const string GitHubWrite = "github_write";
    public const string GitHubCreatePr = "github_create_pr";

    public static readonly IReadOnlyList<string> All = new[]
    {
        HttpFetch, DownloadToWorkspace, WebSearch, ExtractText, GitClone, SandboxRun, GitHubRead, GitHubWrite, GitHubCreatePr,
    };

    /// <summary>Tools the gateway always requires an approval for unless the operator says otherwise (Q2).</summary>
    public static readonly IReadOnlyList<string> DefaultAlwaysApprove = new[] { SandboxRun, GitHubWrite, GitHubCreatePr };
}

/// <summary>
/// An agent asks the gateway to run a broker tool (<c>POST /agent/tools/{tool}</c>). The call
/// belongs to a claimed turn. <c>ApprovalId</c> is required for always-approve tools; the gateway
/// recomputes the arguments hash itself and never takes it from this request.
/// </summary>
public sealed record InvokeToolRequest(
    Guid CorrelationId,
    Guid ThreadId,
    string ToolCallId,
    JsonElement Arguments,
    Guid? ApprovalId = null);

/// <summary>
/// The SHA-256 of a tool call's arguments in canonical JSON, binding an approval to exactly the
/// call the human saw. The agent and the gateway compute it with this same code.
/// </summary>
public static class ToolArgsHash
{
    /// <summary>Lowercase hex SHA-256 of <see cref="Canonical"/>.</summary>
    public static string Compute(JsonElement args) =>
        Convert.ToHexStringLower(SHA256.HashData(Canonical(args)));

    /// <summary>Lowercase hex SHA-256 of several parts, for an approval that also covers other data.</summary>
    public static string Combine(params string[] parts) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", parts))));

    public static bool IsValid(string? hash) =>
        hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// Compact UTF-8 JSON with object properties in ordinal order, numbers as written, and strings
    /// re-encoded, so formatting and key order don't change the hash but any value does.
    /// </summary>
    public static byte[] Canonical(JsonElement args)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            Write(writer, args);
        return buffer.ToArray();
    }

    private static void Write(Utf8JsonWriter w, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    w.WritePropertyName(p.Name);
                    Write(w, p.Value);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in e.EnumerateArray())
                    Write(w, item);
                w.WriteEndArray();
                break;
            case JsonValueKind.String:
                w.WriteStringValue(e.GetString());
                break;
            case JsonValueKind.Number:
                w.WriteRawValue(e.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                w.WriteBooleanValue(e.GetBoolean());
                break;
            default:
                w.WriteNullValue();
                break;
        }
    }
}
