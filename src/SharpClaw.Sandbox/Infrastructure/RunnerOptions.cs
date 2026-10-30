namespace SharpClaw.Sandbox.Infrastructure;

/// <summary>The runner's settings, from its environment (set by compose).</summary>
public sealed class RunnerOptions
{
    /// <summary>The service secret the gateway presents (<c>SANDBOX_SECRET</c>). Required.</summary>
    public string Secret { get; init; } = "";

    public int Port { get; init; } = 8090;
    public string WorkspacePath { get; init; } = "/workspace";
    public string CachePath { get; init; } = "/cache";

    /// <summary>The egress proxy, with the sandbox's own credential (<c>HTTPS_PROXY</c>); null means direct (tests).</summary>
    public Uri? Proxy { get; init; }

    public const int MinSecretChars = 16;

    public static RunnerOptions FromEnvironment()
    {
        var proxy = Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? Environment.GetEnvironmentVariable("https_proxy");
        return new RunnerOptions
        {
            Secret = Environment.GetEnvironmentVariable("SANDBOX_SECRET") ?? "",
            Port = int.TryParse(Environment.GetEnvironmentVariable("SANDBOX_PORT"), out var port) ? port : 8090,
            WorkspacePath = Environment.GetEnvironmentVariable("SANDBOX_WORKSPACE") ?? "/workspace",
            CachePath = Environment.GetEnvironmentVariable("SANDBOX_CACHE") ?? "/cache",
            Proxy = string.IsNullOrWhiteSpace(proxy) ? null : new Uri(proxy),
        };
    }
}
