using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;

namespace SharpClaw.Gateway.Configuration;

public sealed class ListenerOptions
{
    public const string Section = "Listeners";
    [Range(1, 65535)] public int ClientPort { get; set; } = 8080;
    [Range(1, 65535)] public int AgentPort { get; set; } = 8081;
}

public sealed class DatabaseOptions
{
    public const string Section = "Database";
    [Required] public string Path { get; set; } = "/data/sharpclaw.db";
}

public sealed class AuthOptions
{
    public const string Section = "Auth";
    public const int MinPbkdf2Iterations = 600_000;

    [Required(ErrorMessage = "Auth:AdminKey is required (set Auth__AdminKey).")]
    public string AdminKey { get; set; } = "";

    [ValidateObjectMembers] public SessionCookieOptions Session { get; set; } = new();
    [ValidateObjectMembers] public PasswordOptions Password { get; set; } = new();
    [ValidateObjectMembers] public LoginOptions Login { get; set; } = new();

    /// <summary>
    /// Extra origins accepted for cookie-authenticated requests (a reverse proxy whose public origin
    /// differs from what the gateway sees). Not a CORS allow-list.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];

    public ForwardedHeadersSettings ForwardedHeaders { get; set; } = new();
}

public enum CookieSecureMode { Auto, Always, Never }

public sealed class SessionCookieOptions
{
    [Required] public string CookieName { get; set; } = "sharpclaw_session";

    /// <summary><c>Auto</c>: Secure when the request is HTTPS (after forwarded-header processing).</summary>
    public CookieSecureMode CookieSecure { get; set; } = CookieSecureMode.Auto;

    [Range(1, 24 * 365)] public int IdleTimeoutHours { get; set; } = 168;
    [Range(1, 3650)] public int AbsoluteTimeoutDays { get; set; } = 30;
}

public sealed class PasswordOptions
{
    [Range(AuthOptions.MinPbkdf2Iterations, int.MaxValue,
        ErrorMessage = "Auth:Password:Pbkdf2Iterations must be at least 600000.")]
    public int Pbkdf2Iterations { get; set; } = AuthOptions.MinPbkdf2Iterations;
}

public sealed class LoginOptions
{
    [Range(1, 1000)] public int MaxFailures { get; set; } = 5;
    [Range(1, 24 * 60)] public int WindowMinutes { get; set; } = 15;
}

public sealed class ForwardedHeadersSettings
{
    public bool Enabled { get; set; }

    /// <summary>Only these proxy addresses may set X-Forwarded-For/Proto/Host.</summary>
    public string[] KnownProxies { get; set; } = [];
}

public sealed class ProviderOptions
{
    public const string Section = "Provider";
    [Required] public string Type { get; set; } = "OpenRouter";
    [Range(1, 600)] public int ConnectTimeoutSeconds { get; set; } = 10;
    [Range(1, 3600)] public int IdleTimeoutSeconds { get; set; } = 60;
}

public sealed class OpenRouterOptions
{
    public const string Section = "OpenRouter";
    [Required, Url] public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string ApiKey { get; set; } = "";
}

public sealed class FakeOptions
{
    public const string Section = "Fake";
    public string? ScriptPath { get; set; }
    [Range(0, 1)] public double CostPerTokenUsd { get; set; } = 0.000001;
}

public sealed class InboxOptions
{
    public const string Section = "Inbox";
    [Range(0, 600)] public int MaxWaitSeconds { get; set; } = 60;
    [Range(1, 86400)] public int ClaimTimeoutSeconds { get; set; } = 300;
}

public sealed class MessagesOptions
{
    public const string Section = "Messages";
    [Range(1, 10_000_000)] public int MaxContentChars { get; set; } = 32000;
}

public sealed class ApprovalsOptions
{
    public const string Section = "Approvals";

    /// <summary>A pending approval expires (counts as denied) after this many seconds.</summary>
    [Range(1, 7 * 86400)] public int TimeoutSeconds { get; set; } = 300;

    /// <summary>How often the background sweeper expires overdue approvals.</summary>
    [Range(1, 86400)] public int SweepIntervalSeconds { get; set; } = 10;
}

public sealed class EventsOptions
{
    public const string Section = "Events";

    /// <summary>Durable SSE events older than this are pruned (replay before that yields <c>reset</c>).</summary>
    [Range(1, 3650)] public int RetentionDays { get; set; } = 14;

    /// <summary>
    /// Live events buffered per SSE subscriber. A subscriber that falls this far behind is
    /// disconnected and recovers by reconnecting with <c>Last-Event-ID</c>.
    /// </summary>
    [Range(16, 1_000_000)] public int SubscriberBufferSize { get; set; } = 1000;
}

public sealed class ToolsOptions
{
    public const string Section = "Tools";

    /// <summary>Stored tool results are cut to this many characters.</summary>
    [Range(1, int.MaxValue)] public int MaxStoredResultChars { get; set; } = ToolHistoryLimits.DefaultMaxStoredResultChars;

    /// <summary>Each JSON string value in stored tool arguments is cut to this many characters.</summary>
    [Range(1, int.MaxValue)] public int MaxStoredArgumentStringChars { get; set; } = ToolHistoryLimits.DefaultMaxStoredArgumentStringChars;

    [Range(1, 1000)] public int MaxExchangesPerReply { get; set; } = 50;
}

public sealed class ContextOptions
{
    public const string Section = "Context";

    [Range(ContextDefaults.MinWindowTokens, ContextDefaults.MaxWindowTokens)]
    public int DefaultWindowTokens { get; set; } = ContextDefaults.DefaultWindowTokens;

    /// <summary>Kept free for the model's reply.</summary>
    [Range(0, ContextDefaults.MaxWindowTokens)] public int ReplyReserveTokens { get; set; } = 2000;

    [Range(1, 100)] public int CharsPerToken { get; set; } = ContextDefaults.CharsPerToken;
    [Range(0, 1000)] public int MessageOverheadTokens { get; set; } = ContextDefaults.MessageOverheadTokens;

    public int WindowFor(int? agentWindow) => agentWindow ?? DefaultWindowTokens;
}

/// <summary>
/// Model ids a thread may switch to (3.3.1 spec section 3.3). Empty: no per-thread override, every
/// thread uses its agent's model.
/// </summary>
public sealed class ModelsOptions
{
    public const string Section = "Models";
    public const int MaxModelIdChars = 200;

    public string[] Allowed { get; set; } = [];

    /// <summary>Null when valid; otherwise the validation message.</summary>
    public string? Problem()
    {
        if (Allowed.Any(string.IsNullOrWhiteSpace))
            return "Models:Allowed must not contain empty entries.";
        if (Allowed.Any(m => m.Length > MaxModelIdChars))
            return $"Models:Allowed entries must be at most {MaxModelIdChars} characters.";
        if (Allowed.Distinct(StringComparer.Ordinal).Count() != Allowed.Length)
            return "Models:Allowed must not contain duplicates.";
        return null;
    }
}

public sealed class UiOptions
{
    public const string Section = "Ui";

    /// <summary>Folder holding the built web UI; null means <c>wwwroot</c> next to the gateway binaries.</summary>
    public string? RootPath { get; set; }
}

public sealed class ChatOptions
{
    public const string Section = "Chat";

    /// <summary>
    /// Backstop against runaway tool loops: the most LLM calls one user message may make,
    /// whatever the agent's own limit says (3.2 spec section 8.1).
    /// </summary>
    [Range(1, 1000)] public int MaxLlmCallsPerMessage { get; set; } = 20;
}
