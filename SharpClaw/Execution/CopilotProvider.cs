using System.Collections.Concurrent;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using SharpClaw.Abstractions;
using SharpClaw.Models;

namespace SharpClaw.Execution;

public sealed class CopilotProvider(ILogger<CopilotProvider> logger) : ILlmProvider
{
    public string ProviderName => "copilot";

    private readonly Lock _clientLock = new();
    private CopilotClient? _client;
    private readonly ConcurrentDictionary<string, List<ToolAIFunctionAdapter>> _sessionAdapters = new();

    public async Task<ILlmSession> CreateSessionAsync(LlmSessionRequest request, CancellationToken ct = default)
    {
        var client = await GetOrCreateClientAsync(ct);

        var tools = request.Tools?.ToList() ?? [];
        var mcpServers = request.McpServers is not null
            ? request.McpServers.ToDictionary(kvp => kvp.Key, kvp => ToSdkConfig(kvp.Value))
            : new Dictionary<string, McpServerConfig>();

        CopilotSession session;

        if (!string.IsNullOrEmpty(request.ResumeSessionId))
        {
            var resumeConfig = new ResumeSessionConfig
            {
                OnPermissionRequest = PermissionHandler.ApproveAll,
                Tools = tools,
                McpServers = mcpServers,
            };
            if (!string.IsNullOrEmpty(request.Model))
                resumeConfig.Model = request.Model;
            if (request.SystemPrompt is not null)
                resumeConfig.SystemMessage = new SystemMessageConfig { Content = request.SystemPrompt };
            session = await client.ResumeSessionAsync(request.ResumeSessionId, resumeConfig, ct);
        }
        else
        {
            var config = new SessionConfig
            {
                OnPermissionRequest = PermissionHandler.ApproveAll,
                Tools = tools,
                McpServers = mcpServers,
            };
            if (!string.IsNullOrEmpty(request.Model))
                config.Model = request.Model;
            if (request.SystemPrompt is not null)
                config.SystemMessage = new SystemMessageConfig { Content = request.SystemPrompt };
            session = await client.CreateSessionAsync(config, ct);
        }

        // Track tool adapters for scheduling context refresh
        var adapters = tools.OfType<ToolAIFunctionAdapter>().ToList();
        _sessionAdapters[session.SessionId] = adapters;

        return new CopilotLlmSession(session);
    }

    public async Task<AgentRunResult> SendAsync(ILlmSession session, string prompt, CancellationToken ct = default)
    {
        if (session is not CopilotLlmSession copilotSession)
            return AgentRunResult.Fail("Invalid session type for Copilot provider.");

        try
        {
            // Refresh scheduling context on tool adapters before each send
            if (_sessionAdapters.TryGetValue(session.SessionId, out var adapters))
            {
                foreach (var adapter in adapters)
                    adapter.CaptureSchedulingContext();
            }

            var result = await copilotSession.Inner.SendAndWaitAsync(
                new MessageOptions { Prompt = prompt },
                TimeSpan.FromMinutes(5),
                ct);

            var content = result?.Data?.Content ?? string.Empty;
            logger.LogDebug("Copilot response received ({Length} chars)", content.Length);
            return AgentRunResult.Ok(content, session.SessionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Copilot send failed");
            return AgentRunResult.Fail(ex.Message);
        }
    }

    internal void RemoveSession(string sessionId) =>
        _sessionAdapters.TryRemove(sessionId, out _);

    private async Task<CopilotClient> GetOrCreateClientAsync(CancellationToken ct)
    {
        if (_client is not null) return _client;

        lock (_clientLock)
        {
            if (_client is not null) return _client;
            _client = new CopilotClient(new CopilotClientOptions { UseLoggedInUser = true });
        }

        await _client.StartAsync(ct);
        logger.LogInformation("CopilotClient started");
        return _client;
    }

    private static McpServerConfig ToSdkConfig(McpServerDefinition def)
    {
        if (def.Transport.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            return new McpHttpServerConfig
            {
                Url = def.Url ?? string.Empty,
                Headers = def.Headers is not null ? new Dictionary<string, string>(def.Headers) : null,
            };
        }

        return new McpStdioServerConfig
        {
            Command = def.Command ?? string.Empty,
            Args = def.Args?.ToList() ?? [],
            Env = def.Env is not null ? new Dictionary<string, string>(def.Env) : [],
        };
    }

    private sealed class CopilotLlmSession(CopilotSession inner) : ILlmSession
    {
        public CopilotSession Inner { get; } = inner;
        public string SessionId => Inner.SessionId;
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }
}
