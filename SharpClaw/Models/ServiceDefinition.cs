namespace SharpClaw.Models;

public sealed record ServiceDefinition
{
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public required string Project { get; init; }

    /// <summary>
    /// Runtime type: "dotnet", "node", "python", or "docker-compose".
    /// docker-compose services don't spawn a separate process — they just run compose up/down.
    /// </summary>
    public string Runtime { get; init; } = "dotnet";

    public int Port { get; init; }
    public bool AutoStart { get; init; } = true;

    /// <summary>
    /// Names of other services that must be healthy before this one starts.
    /// </summary>
    public IReadOnlyList<string>? Depends { get; init; }

    /// <summary>
    /// Docker Compose configuration. Required when runtime is "docker-compose",
    /// optional for other runtimes (used to bring up supporting infrastructure).
    /// </summary>
    public ServiceComposeDefinition? Compose { get; init; }

    /// <summary>
    /// Health check configuration. Defaults to HTTP GET on HealthEndpoint for process-based services.
    /// </summary>
    public ServiceHealthCheck HealthCheck { get; init; } = new();

    public ServiceExposeDefinition Expose { get; init; } = new();
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

public sealed record ServiceComposeDefinition
{
    /// <summary>
    /// Path to docker-compose.yml relative to the service project directory.
    /// </summary>
    public string File { get; init; } = "docker-compose.yml";

    /// <summary>
    /// Specific services to start. Null or empty means all services in the compose file.
    /// </summary>
    public IReadOnlyList<string>? Services { get; init; }

    /// <summary>
    /// Whether to run `docker compose down` when the service is stopped.
    /// </summary>
    public bool StopOnShutdown { get; init; } = true;
}

public sealed record ServiceHealthCheck
{
    /// <summary>
    /// Health check type: "http" (default for process runtimes) or "tcp" (default for docker-compose).
    /// </summary>
    public string Type { get; init; } = "http";

    /// <summary>
    /// HTTP path for http health checks. Ignored for tcp.
    /// </summary>
    public string Path { get; init; } = "/health";

    /// <summary>
    /// Host to connect to. Defaults to "localhost".
    /// </summary>
    public string Host { get; init; } = "localhost";

    /// <summary>
    /// Port to check. If 0, uses the service's Port field.
    /// </summary>
    public int Port { get; init; }

    /// <summary>
    /// Seconds to wait for the service to become healthy after starting.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// Seconds between health check retries during startup.
    /// </summary>
    public int IntervalSeconds { get; init; } = 2;
}

public sealed record ServiceExposeDefinition
{
    public bool Mcp { get; init; }
    public IReadOnlyList<ServiceEndpointDefinition> Endpoints { get; init; } = [];
}

public sealed record ServiceEndpointDefinition
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public string Method { get; init; } = "GET";
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<ServiceEndpointParameter>? Parameters { get; init; }
}

public sealed record ServiceEndpointParameter
{
    public required string Name { get; init; }
    public string Type { get; init; } = "string";
    public string Description { get; init; } = string.Empty;
    public bool Required { get; init; }
}
