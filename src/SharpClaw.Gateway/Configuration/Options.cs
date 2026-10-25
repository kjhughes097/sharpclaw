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
