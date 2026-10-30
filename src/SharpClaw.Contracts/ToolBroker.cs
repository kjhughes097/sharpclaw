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
/// The operator's broker settings for the calling agent (<c>GET /agent/tools</c>): which broker
/// tools it may call, which always need an approval, the <c>http_fetch</c> hosts scored low, and
/// the hosts <c>git_clone</c> may clone from.
/// </summary>
public sealed record AgentToolPolicyResponse(
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> AlwaysApprove,
    IReadOnlyList<string> FetchAllowHosts,
    IReadOnlyList<string>? CloneAllowHosts = null);

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

/// <summary>
/// The URL rule for network tools (spec section 7.1): absolute <c>http</c> or <c>https</c>, a host,
/// no credentials in the URL, at most 2,048 characters. Applied by the agent, the gateway and the
/// sandbox runner alike; the egress proxy then checks where the host actually resolves.
/// </summary>
public static class ToolUrls
{
    public const int MaxLength = 2048;

    public static (Uri? Url, string? Error) Parse(string? value, params string[] schemes)
    {
        schemes = schemes.Length == 0 ? ["http", "https"] : schemes;
        if (string.IsNullOrWhiteSpace(value))
            return (null, "url is required");
        if (value.Length > MaxLength)
            return (null, $"url is longer than {MaxLength} characters");
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var url) || !schemes.Contains(url.Scheme))
            return (null, $"url must be an absolute {string.Join(" or ", schemes)} URL");
        if (string.IsNullOrEmpty(url.Host))
            return (null, "url must have a host");
        if (!string.IsNullOrEmpty(url.UserInfo))
            return (null, "credentials in the URL are not allowed");
        return (url, null);
    }

    /// <summary>
    /// A clone URL (spec section 7.4): <c>https://host/owner/repo[.git]</c>, nothing else (no query,
    /// fragment, port or credentials). Returns the host and the repository path (<c>owner/repo</c>,
    /// or <c>group/sub/repo</c> on GitLab), or an error.
    /// </summary>
    public static (string? Host, string? Repo, string? Error) ParseGitRepo(string? value)
    {
        var (url, error) = Parse(value, "https");
        if (url is null)
            return (null, null, error);
        if (!url.IsDefaultPort || url.Query.Length > 0 || url.Fragment.Length > 0)
            return (null, null, "url must be https://host/owner/repo with no port, query or fragment");
        var path = url.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.Ordinal))
            path = path[..^4];
        var segments = path.Split('/');
        if (segments.Length < 2 || segments.Any(s => s.Length == 0 || s is "." or ".." ||
                !s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            return (null, null, "url must name a repository, e.g. https://github.com/owner/repo");
        return (url.IdnHost.ToLowerInvariant(), path, null);
    }

    /// <summary>True when <paramref name="host"/> is <paramref name="entry"/> or one of its subdomains.</summary>
    public static bool HostMatches(string host, string entry)
    {
        var e = entry.Trim().TrimEnd('.').ToLowerInvariant();
        var h = host.TrimEnd('.').ToLowerInvariant();
        return e.Length > 0 && (h == e || h.EndsWith("." + e, StringComparison.Ordinal));
    }
}

/// <summary>
/// The fixed GitHub operations (spec section 7.6): there is no generic "call any URL" operation,
/// and owner/repository names are validated before anything is sent.
/// </summary>
public static class GitHubOperations
{
    public const string GetIssue = "get_issue";
    public const string ListIssues = "list_issues";
    public const string GetPull = "get_pull";
    public const string GetPullDiff = "get_pull_diff";
    public const string ListComments = "list_comments";
    public const string GetFile = "get_file";

    public const string Comment = "comment";
    public const string CreateIssue = "create_issue";
    public const string AddLabels = "add_labels";
    public const string RemoveLabels = "remove_labels";

    public static readonly IReadOnlyList<string> Read = [GetIssue, ListIssues, GetPull, GetPullDiff, ListComments, GetFile];
    public static readonly IReadOnlyList<string> Write = [Comment, CreateIssue, AddLabels, RemoveLabels];

    /// <summary>Operations that need an issue or pull request number.</summary>
    public static readonly IReadOnlyList<string> NeedNumber = [GetIssue, GetPull, GetPullDiff, ListComments, Comment, AddLabels, RemoveLabels];

    /// <summary>A GitHub user or organisation name.</summary>
    public static bool IsOwner(string? s) =>
        s is { Length: > 0 and <= 39 } && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && !s.StartsWith('-');

    /// <summary>A repository name.</summary>
    public static bool IsRepo(string? s) =>
        s is { Length: > 0 and <= 100 } && s is not ("." or "..") && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

/// <summary>Limits on a <c>github_create_pr</c> change set (spec section 7.7).</summary>
public static class ChangeSetLimits
{
    public const int MaxFiles = 100;
    public const int MaxFileBytes = 1024 * 1024;
    public const int MaxTotalBytes = 5 * 1024 * 1024;

    public const string ModeFile = "100644";
    public const string ModeExecutable = "100755";
    public const string ModeSymlink = "120000";
    public const string ModeSubmodule = "160000";
}
