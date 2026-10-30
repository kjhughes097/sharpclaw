using System.ComponentModel.DataAnnotations;

namespace SharpClaw.Gateway.Configuration;

/// <summary>
/// The egress proxy listener (external tools spec section 5): the sandboxes' only route to the
/// internet. It is bound only when at least one sandbox credential is configured, and is never
/// published to the host.
/// </summary>
public sealed class EgressOptions : IHasProblem
{
    public const string Section = "Egress";
    public const int MinSecretChars = 16;

    [Range(1, 65535)] public int Port { get; set; } = 8082;

    /// <summary>Per sandbox (by agent name): the proxy credential it presents as <c>sbx-&lt;name&gt;:&lt;secret&gt;</c>.</summary>
    public Dictionary<string, EgressSandboxOptions> Sandboxes { get; set; } = new();

    // Limits (Q7), so a sandbox can't starve the gateway.
    [Range(1, 10_000)] public int MaxConnectionsPerSandbox { get; set; } = 32;
    [Range(1, 3600)] public int IdleTimeoutSeconds { get; set; } = 60;
    [Range(1, 600)] public int HeadTimeoutSeconds { get; set; } = 30;
    [Range(1, 600)] public int ConnectTimeoutSeconds { get; set; } = 10;
    [Range(1024, long.MaxValue)] public long MaxBytesPerConnection { get; set; } = 1L << 30;
    [Range(1024, 1024 * 1024)] public int MaxHeadBytes { get; set; } = 64 * 1024;

    /// <summary>Hosts never allowed, matched exactly or as a parent domain (<c>example.com</c> also blocks <c>a.example.com</c>).</summary>
    public string[] DenyHosts { get; set; } = [];

    /// <summary>Sandboxes with a credential. An empty secret means "not set up yet" (the template's default).</summary>
    public IEnumerable<KeyValuePair<string, EgressSandboxOptions>> Configured =>
        Sandboxes.Where(s => s.Value.Secret.Length > 0);

    public bool Enabled => Configured.Any();

    public string? Problem()
    {
        foreach (var (name, sandbox) in Sandboxes)
            if (sandbox.Secret is { Length: > 0 and < MinSecretChars })
                return $"Egress:Sandboxes:{name}:Secret must be at least {MinSecretChars} characters.";
        if (DenyHosts.Any(string.IsNullOrWhiteSpace))
            return "Egress:DenyHosts must not contain empty entries.";
        return null;
    }
}

public sealed class EgressSandboxOptions
{
    /// <summary><c>SANDBOX_&lt;NAME&gt;_PROXY_SECRET</c>; must differ from the runner's service secret.</summary>
    public string Secret { get; set; } = "";
}
