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
        var requestedMcpNames = request.McpServers?.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        logger.LogInformation(
            "Copilot session setup: model={Model}, resume={Resume}, tools={ToolCount}, mcps={McpCount} ({McpNames})",
            request.Model ?? "<default>",
            !string.IsNullOrEmpty(request.ResumeSessionId),
            tools.Count,
            requestedMcpNames.Count,
            requestedMcpNames.Count == 0 ? "none" : string.Join(", ", requestedMcpNames));

        if (request.McpServers is not null)
        {
            foreach (var (name, def) in request.McpServers)
            {
                if (def.Transport.Equals("http", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("MCP {Name} transport=http url={Url}", name, def.Url ?? "<empty>");
                }
                else
                {
                    logger.LogInformation("MCP {Name} transport=stdio command={Command}", name, def.Command ?? "<empty>");
                }
            }
        }

        var mcpServers = request.McpServers is not null
            ? request.McpServers.ToDictionary(kvp => kvp.Key, kvp => ToSdkConfig(kvp.Value))
            : new Dictionary<string, McpServerConfig>();

        CopilotSession session;

        try
        {
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
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Copilot session creation failed (resume={Resume}, tools={ToolCount}, mcps={McpCount}: {McpNames})",
                !string.IsNullOrEmpty(request.ResumeSessionId),
                tools.Count,
                requestedMcpNames.Count,
                requestedMcpNames.Count == 0 ? "none" : string.Join(", ", requestedMcpNames));
            throw;
        }

        logger.LogInformation("Copilot session created: {SessionId}", session.SessionId);

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

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            logger.LogInformation(
                "Copilot SendAndWaitAsync starting (session={SessionId}, promptLength={PromptLength})",
                session.SessionId, prompt.Length);

            var result = await copilotSession.Inner.SendAndWaitAsync(
                new MessageOptions { Prompt = prompt },
                TimeSpan.FromMinutes(10),
                ct);

            stopwatch.Stop();
            var content = result?.Data?.Content ?? string.Empty;
            logger.LogInformation(
                "Copilot response received (session={SessionId}, {Length} chars, elapsed={Elapsed})",
                session.SessionId, content.Length, stopwatch.Elapsed);
            return AgentRunResult.Ok(content, session.SessionId);
        }
        catch (TimeoutException ex)
        {
            logger.LogError(
                ex,
                "Copilot SendAndWaitAsync timed out (session={SessionId}). The LLM or a tool call may be unresponsive.",
                session.SessionId);
            return AgentRunResult.Fail($"SendAndWaitAsync timed out after 10 minutes (session={session.SessionId}). Check if an MCP server or tool is hanging.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Copilot send failed (session={SessionId})", session.SessionId);
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
                Tools = ["*"],
            };
        }

        return new McpStdioServerConfig
        {
            Command = def.Command ?? string.Empty,
            Args = def.Args?.ToList() ?? [],
            Env = def.Env is not null ? new Dictionary<string, string>(def.Env) : [],
            Tools = ["*"],
        };
    }

    private sealed class CopilotLlmSession(CopilotSession inner) : ILlmSession
    {
        public CopilotSession Inner { get; } = inner;
        public string SessionId => Inner.SessionId;
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }
}
