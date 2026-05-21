using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using SharpClaw.Abstractions;
using SharpClaw.Configuration;
using SharpClaw.Loading;
using SharpClaw.Models;
using SharpClaw.Tools;

namespace SharpClaw.Workers;

public sealed class ServiceRunner(
    IServiceRegistry serviceRegistry,
    IToolRegistry toolRegistry,
    ServiceLoader serviceLoader,
    IOptions<SharpClawOptions> options,
    IHostEnvironment env,
    ILogger<ServiceRunner> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, ManagedService> _running = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Load and register service definitions
        serviceRegistry.Clear();
        foreach (var definition in serviceLoader.Load())
        {
            serviceRegistry.Register(definition);
            logger.LogInformation("Registered service {Name} (runtime={Runtime}, port={Port})",
                definition.Name, definition.Runtime, definition.Port);
        }

        // Start all autoStart services in dependency order
        var autoStartServices = serviceRegistry.GetAll().Where(s => s.AutoStart).ToList();
        if (autoStartServices.Count == 0)
        {
            logger.LogInformation("No auto-start services configured");
            return;
        }

        var ordered = TopologicalSort(autoStartServices, serviceRegistry);
        foreach (var definition in ordered)
        {
            try
            {
                await StartServiceAsync(definition, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to start service {Name}", definition.Name);
            }
        }

        // Health monitoring loop
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await CheckHealthAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down
        }
    }

    public async Task StartServiceAsync(ServiceDefinition definition, CancellationToken ct)
    {
        if (_running.ContainsKey(definition.Name))
        {
            logger.LogWarning("Service {Name} is already running", definition.Name);
            return;
        }

        // Verify dependencies are healthy
        if (definition.Depends is { Count: > 0 })
        {
            foreach (var dep in definition.Depends)
            {
                if (!_running.TryGetValue(dep, out var depManaged) || depManaged.Status != ServiceStatus.Healthy)
                {
                    logger.LogError("Service {Name} depends on {Dependency} which is not healthy; skipping",
                        definition.Name, dep);
                    return;
                }
            }
        }

        if (IsComposeRuntime(definition))
        {
            await StartComposeServiceAsync(definition, ct);
        }
        else
        {
            await StartProcessServiceAsync(definition, ct);
        }
    }

    public void StopService(string name)
    {
        if (!_running.TryRemove(name, out var managed))
        {
            logger.LogWarning("Service {Name} is not running", name);
            return;
        }

        // Stop the process if it's a process-based service
        if (managed.Process is not null)
        {
            try
            {
                if (!managed.Process.HasExited)
                {
                    managed.Process.Kill(entireProcessTree: true);
                    managed.Process.WaitForExit(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error stopping service process {Name}", name);
            }
            finally
            {
                managed.Process.Dispose();
            }
        }

        // Stop Docker Compose if configured
        if (managed.Definition.Compose is { StopOnShutdown: true })
        {
            StopCompose(managed.Definition);
        }

        managed.Status = ServiceStatus.Stopped;
        logger.LogInformation("Service {Name} stopped", name);
    }

    public IReadOnlyDictionary<string, ManagedService> GetRunningServices() =>
        _running.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

    // --- Docker Compose runtime ---

    private async Task StartComposeServiceAsync(ServiceDefinition definition, CancellationToken ct)
    {
        await RunComposeUpAsync(definition, ct);

        var managed = new ManagedService(definition, process: null);
        _running[definition.Name] = managed;

        logger.LogInformation("Started compose service {Name}", definition.Name);

        var healthy = await WaitForHealthyAsync(managed, ct);
        if (healthy)
        {
            managed.Status = ServiceStatus.Healthy;
            RegisterToolsForService(definition);
            logger.LogInformation("Compose service {Name} is healthy", definition.Name);
        }
        else
        {
            managed.Status = ServiceStatus.Unhealthy;
            logger.LogWarning("Compose service {Name} did not become healthy within timeout", definition.Name);
        }
    }

    // --- Process-based runtime ---

    private async Task StartProcessServiceAsync(ServiceDefinition definition, CancellationToken ct)
    {
        // Start supporting compose infrastructure if configured
        if (definition.Compose is not null)
        {
            await RunComposeUpAsync(definition, ct);
        }

        var process = StartProcess(definition);
        var managed = new ManagedService(definition, process);
        _running[definition.Name] = managed;

        logger.LogInformation("Starting service {Name} (PID={Pid}) on port {Port}",
            definition.Name, process.Id, definition.Port);

        var healthy = await WaitForHealthyAsync(managed, ct);
        if (healthy)
        {
            managed.Status = ServiceStatus.Healthy;
            RegisterToolsForService(definition);
            logger.LogInformation("Service {Name} is healthy and tools registered", definition.Name);
        }
        else
        {
            managed.Status = ServiceStatus.Unhealthy;
            logger.LogWarning("Service {Name} did not become healthy within timeout", definition.Name);
        }
    }

    private Process StartProcess(ServiceDefinition definition)
    {
        var (command, arguments) = ResolveCommand(definition);

        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = ResolveProjectDirectory(definition),
        };

        // Set port via environment
        if (definition.Port > 0)
            startInfo.EnvironmentVariables["ASPNETCORE_URLS"] = $"http://localhost:{definition.Port}";

        // Apply custom environment variables
        if (definition.Environment is not null)
        {
            foreach (var (key, value) in definition.Environment)
            {
                startInfo.EnvironmentVariables[key] = value;
            }
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                logger.LogDebug("[{Service}] {Output}", definition.Name, e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                logger.LogWarning("[{Service}] {Error}", definition.Name, e.Data);
        };

        process.Exited += (_, _) =>
        {
            logger.LogWarning("Service {Name} process exited (code={ExitCode})",
                definition.Name, process.ExitCode);
            if (_running.TryGetValue(definition.Name, out var managed))
                managed.Status = ServiceStatus.Failed;
        };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process for service '{definition.Name}'");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    private (string Command, string Arguments) ResolveCommand(ServiceDefinition definition) =>
        definition.Runtime.ToLowerInvariant() switch
        {
            "dotnet" => ("dotnet", "run --no-launch-profile"),
            "node" => ("node", "."),
            "python" => ("python", "main.py"),
            _ => throw new InvalidOperationException($"Unsupported process runtime: {definition.Runtime}")
        };

    // --- Health checking ---

    private async Task<bool> WaitForHealthyAsync(ManagedService managed, CancellationToken ct)
    {
        var healthCheck = managed.Definition.HealthCheck;
        var timeout = TimeSpan.FromSeconds(healthCheck.TimeoutSeconds);
        var interval = TimeSpan.FromSeconds(healthCheck.IntervalSeconds);
        var deadline = DateTimeOffset.UtcNow + timeout;
        var checkPort = healthCheck.Port > 0 ? healthCheck.Port : managed.Definition.Port;

        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            // For process-based services, bail if the process died
            if (managed.Process is { HasExited: true })
                return false;

            var isHealthy = healthCheck.Type.ToLowerInvariant() switch
            {
                "tcp" => await CheckTcpHealthAsync(healthCheck.Host, checkPort),
                _ => await CheckHttpHealthAsync(healthCheck.Host, checkPort, healthCheck.Path, ct),
            };

            if (isHealthy)
                return true;

            await Task.Delay(interval, ct);
        }

        return false;
    }

    private async Task CheckHealthAsync(CancellationToken ct)
    {
        foreach (var (name, managed) in _running)
        {
            // For process-based services, check if process died
            if (managed.Process is { HasExited: true })
            {
                managed.Status = ServiceStatus.Failed;
                continue;
            }

            var healthCheck = managed.Definition.HealthCheck;
            var checkPort = healthCheck.Port > 0 ? healthCheck.Port : managed.Definition.Port;

            var isHealthy = healthCheck.Type.ToLowerInvariant() switch
            {
                "tcp" => await CheckTcpHealthAsync(healthCheck.Host, checkPort),
                _ => await CheckHttpHealthAsync(healthCheck.Host, checkPort, healthCheck.Path, ct),
            };

            managed.Status = isHealthy ? ServiceStatus.Healthy : ServiceStatus.Unhealthy;
        }
    }

    private static async Task<bool> CheckHttpHealthAsync(string host, int port, string path, CancellationToken ct)
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await httpClient.GetAsync($"http://{host}:{port}{path}", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> CheckTcpHealthAsync(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // --- Docker Compose helpers ---

    private async Task RunComposeUpAsync(ServiceDefinition definition, CancellationToken ct)
    {
        var compose = definition.Compose!;
        var projectDir = ResolveProjectDirectory(definition);
        var composeFile = Path.Combine(projectDir, compose.File);

        if (!File.Exists(composeFile))
        {
            logger.LogWarning("Compose file not found at {Path} for service {Name}; skipping",
                composeFile, definition.Name);
            return;
        }

        var args = $"compose -f \"{composeFile}\" up -d";
        if (compose.Services is { Count: > 0 })
        {
            args += " " + string.Join(" ", compose.Services);
        }

        logger.LogInformation("Running docker compose up for service {Name}", definition.Name);

        var exitCode = await RunDockerCommandAsync(args, projectDir, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Docker Compose failed for service '{definition.Name}' (exit code {exitCode})");
        }
    }

    private void StopCompose(ServiceDefinition definition)
    {
        var compose = definition.Compose!;
        var projectDir = ResolveProjectDirectory(definition);
        var composeFile = Path.Combine(projectDir, compose.File);

        if (!File.Exists(composeFile))
            return;

        var args = $"compose -f \"{composeFile}\" down";
        logger.LogInformation("Stopping Docker Compose for service {Name}", definition.Name);

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = args,
                WorkingDirectory = projectDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            process?.WaitForExit(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to stop Docker Compose for service {Name}", definition.Name);
        }
    }

    private static async Task<int> RunDockerCommandAsync(string args, string workingDirectory, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = args,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start docker process");

        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    // --- Utility ---

    private string ResolveProjectDirectory(ServiceDefinition definition)
    {
        var workspacePath = options.Value.WorkspacePath;
        if (string.IsNullOrEmpty(workspacePath))
            workspacePath = env.ContentRootPath;

        var projectPath = Path.Combine(workspacePath, definition.Project);
        if (!Directory.Exists(projectPath))
            throw new DirectoryNotFoundException(
                $"Service project directory not found: {projectPath}");

        return projectPath;
    }

    private void RegisterToolsForService(ServiceDefinition definition)
    {
        foreach (var endpoint in definition.Expose.Endpoints)
        {
            var tool = new HttpProxyTool(definition.Name, endpoint, definition.Port);
            toolRegistry.Register(tool);
            logger.LogDebug("Registered proxy tool: {ToolName}", tool.Name);
        }
    }

    private static bool IsComposeRuntime(ServiceDefinition definition) =>
        definition.Runtime.Equals("docker-compose", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Topological sort of services based on their Depends declarations.
    /// Services with no dependencies come first.
    /// </summary>
    private static IReadOnlyList<ServiceDefinition> TopologicalSort(
        List<ServiceDefinition> services,
        IServiceRegistry registry)
    {
        var result = new List<ServiceDefinition>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lookup = services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        void Visit(ServiceDefinition service)
        {
            if (visited.Contains(service.Name)) return;
            if (visiting.Contains(service.Name)) return; // Circular dependency — skip

            visiting.Add(service.Name);

            if (service.Depends is { Count: > 0 })
            {
                foreach (var dep in service.Depends)
                {
                    // Dependency might not be in the autoStart list but is in the registry
                    if (lookup.TryGetValue(dep, out var depService))
                        Visit(depService);
                    else if (registry.Get(dep) is { } registeredDep)
                    {
                        lookup[dep] = registeredDep;
                        Visit(registeredDep);
                    }
                }
            }

            visiting.Remove(service.Name);
            visited.Add(service.Name);
            result.Add(service);
        }

        foreach (var service in services)
            Visit(service);

        return result;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping all managed services...");
        // Stop in reverse order (dependents first)
        foreach (var name in _running.Keys.Reverse().ToList())
        {
            StopService(name);
        }

        await base.StopAsync(cancellationToken);
    }
}
