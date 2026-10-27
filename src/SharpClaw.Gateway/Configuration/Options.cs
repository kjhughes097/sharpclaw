using SharpClaw.Contracts;

namespace SharpClaw.Gateway.Configuration;

public sealed class ListenerOptions
{
    public const string Section = "Listeners";
    public int ClientPort { get; set; } = 8080;
    public int AgentPort { get; set; } = 8081;
}

public sealed class DatabaseOptions
{
    public const string Section = "Database";
    public string Path { get; set; } = "/data/sharpclaw.db";
}

public sealed class AuthOptions
{
    public const string Section = "Auth";
    public string AdminKey { get; set; } = "";
}

public sealed class ProviderOptions
{
    public const string Section = "Provider";
    public string Type { get; set; } = "OpenRouter";
    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int IdleTimeoutSeconds { get; set; } = 60;
}

public sealed class OpenRouterOptions
{
    public const string Section = "OpenRouter";
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string ApiKey { get; set; } = "";
}

public sealed class FakeOptions
{
    public const string Section = "Fake";
    public string? ScriptPath { get; set; }
    public double CostPerTokenUsd { get; set; } = 0.000001;
}

public sealed class InboxOptions
{
    public const string Section = "Inbox";
    public int MaxWaitSeconds { get; set; } = 60;
    public int ClaimTimeoutSeconds { get; set; } = 300;
}

public sealed class MessagesOptions
{
    public const string Section = "Messages";
    public int MaxContentChars { get; set; } = 32000;
}

public sealed class ApprovalsOptions
{
    public const string Section = "Approvals";

    /// <summary>A pending approval expires (counts as denied) after this many seconds.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>How often the background sweeper expires overdue approvals.</summary>
    public int SweepIntervalSeconds { get; set; } = 10;
}

public sealed class EventsOptions
{
    public const string Section = "Events";

    /// <summary>Durable SSE events older than this are pruned (replay before that yields <c>reset</c>).</summary>
    public int RetentionDays { get; set; } = 14;
}

public sealed class ToolsOptions
{
    public const string Section = "Tools";

    /// <summary>Stored tool results are cut to this many characters.</summary>
    public int MaxStoredResultChars { get; set; } = ToolHistoryLimits.DefaultMaxStoredResultChars;

    /// <summary>Each JSON string value in stored tool arguments is cut to this many characters.</summary>
    public int MaxStoredArgumentStringChars { get; set; } = ToolHistoryLimits.DefaultMaxStoredArgumentStringChars;

    public int MaxExchangesPerReply { get; set; } = 50;
}

public sealed class ContextOptions
{
    public const string Section = "Context";

    public int DefaultWindowTokens { get; set; } = ContextDefaults.DefaultWindowTokens;

    /// <summary>Kept free for the model's reply.</summary>
    public int ReplyReserveTokens { get; set; } = 2000;

    public int CharsPerToken { get; set; } = ContextDefaults.CharsPerToken;
    public int MessageOverheadTokens { get; set; } = 4;

    public int WindowFor(int? agentWindow) => agentWindow ?? DefaultWindowTokens;
}

public sealed class ChatOptions
{
    public const string Section = "Chat";

    /// <summary>
    /// Backstop against runaway tool loops: the most LLM calls one user message may make,
    /// whatever the agent's own limit says (3.2 spec section 8.1).
    /// </summary>
    public int MaxLlmCallsPerMessage { get; set; } = 20;
}
