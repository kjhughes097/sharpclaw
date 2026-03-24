using Anthropic;
using ModelContextProtocol.Client;

namespace SharpClaw.Core;

/// <summary>
/// Encapsulates the lifecycle of running an agent: connects MCP servers, builds
/// the permission gate and tool dispatcher, creates the backend, and executes turns.
/// Disposable — cleans up MCP clients and backend on dispose.
/// </summary>
public sealed class AgentRunner : IAsyncDisposable
{
    private sealed record ResolvedTool(McpClient Client, string RawToolName);

    private readonly AgentPersona _persona;
    private readonly IReadOnlyList<McpServerRecord> _mcpServers;
    private readonly Func<AgentPersona, PermissionGate, IAgentBackend>? _backendFactory;
    private readonly List<McpClient> _mcpClients = [];
    private readonly List<ToolSchema> _toolSchemas = [];
    private readonly Dictionary<string, ResolvedTool> _toolClientMap = [];
    private PermissionGate? _permissionGate;
    private AsyncPermissionGate? _asyncPermissionGate;
    private IAgentBackend? _backend;
    private bool _initialized;

    public AgentRunner(
        AgentPersona persona,
        IReadOnlyList<McpServerRecord>? mcpServers = null,
        Func<AgentPersona, PermissionGate, IAgentBackend>? backendFactory = null)
    {
        _persona = persona;
        _mcpServers = mcpServers ?? [];
        _backendFactory = backendFactory;
    }

    public AgentPersona Persona => _persona;
    public IReadOnlyList<ToolSchema> Tools => _toolSchemas;

    /// <summary>
    /// Exposes the async permission gate for resolving pending permission requests (e.g. from the API).
    /// </summary>
    public AsyncPermissionGate? PermissionGate => _asyncPermissionGate;

    /// <summary>
    /// Connects MCP servers, builds the permission gate, and creates the backend.
    /// Must be called before <see cref="SendAsync"/>.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        foreach (var server in _mcpServers)
        {
            var transport = McpServerRegistry.Resolve(server);
            var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
            _mcpClients.Add(client);

            var tools = await client.ListToolsAsync(cancellationToken: ct);
            foreach (var t in tools)
            {
                var namespacedToolName = CreateToolName(server.Slug, t.Name);
                _toolSchemas.Add(t.ToToolSchema(namespacedToolName));
                _toolClientMap[namespacedToolName] = new ResolvedTool(client, t.Name);
            }
        }

        _permissionGate = new PermissionGate(_persona.PermissionPolicy);
        _asyncPermissionGate = new AsyncPermissionGate(_persona.PermissionPolicy);
        _backend = CreateBackend(_persona, _permissionGate);
        _initialized = true;
    }

    /// <summary>
    /// Sends a conversation turn: dispatches to the backend with tools, returns the response text.
    /// </summary>
    public async Task<string> SendAsync(
        IReadOnlyList<ChatMessage> history,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!_initialized || _backend is null)
            throw new InvalidOperationException("Call InitializeAsync before SendAsync.");

        var truncated = HistoryTruncator.Truncate(history, _persona.SystemPrompt);

        return await _backend.CompleteAsync(
            systemPrompt: _persona.SystemPrompt,
            tools: _toolSchemas,
            history: truncated,
            toolDispatcher: ToolDispatcher,
            onProgress: onProgress,
            cancellationToken: ct);
    }

    /// <summary>
    /// Streams a conversation turn as an async sequence of <see cref="AgentEvent"/>s.
    /// Uses the <see cref="AsyncPermissionGate"/> for tool permission checks,
    /// allowing external resolution (e.g. via HTTP).
    /// </summary>
    public IAsyncEnumerable<AgentEvent> StreamAsync(
        IReadOnlyList<ChatMessage> history,
        Action<AgentEvent>? eventSink = null,
        CancellationToken ct = default)
    {
        if (!_initialized || _backend is null)
            throw new InvalidOperationException("Call InitializeAsync before StreamAsync.");

        var truncated = HistoryTruncator.Truncate(history, _persona.SystemPrompt);

        return _backend.StreamAsync(
            systemPrompt: _persona.SystemPrompt,
            tools: _toolSchemas,
            history: truncated,
            toolDispatcher: (call, token) => AsyncToolDispatcher(call, eventSink, token),
            cancellationToken: ct);
    }

    private async Task<ToolCallResult> AsyncToolDispatcher(
        ToolCall call, Action<AgentEvent>? eventSink, CancellationToken ct)
    {
        var allowed = await _asyncPermissionGate!.EvaluateAsync(
            call.Name,
            call.Arguments as IReadOnlyDictionary<string, object?>,
            eventSink,
            ct);

        if (!allowed)
            return new ToolCallResult($"Tool '{call.Name}' was blocked by the permission policy.", IsError: true);

        if (!_toolClientMap.TryGetValue(call.Name, out var resolvedTool))
            return new ToolCallResult($"No MCP client registered for tool '{call.Name}'.", IsError: true);

        var callResult = await resolvedTool.Client.CallToolAsync(resolvedTool.RawToolName, call.Arguments, cancellationToken: ct);
        var resultText = string.Join("\n",
            callResult.Content
                .OfType<ModelContextProtocol.Protocol.TextContentBlock>()
                .Select(t => t.Text));

        return new ToolCallResult(resultText, callResult.IsError ?? false);
    }

    private async Task<ToolCallResult> ToolDispatcher(ToolCall call, CancellationToken ct)
    {
        if (!_permissionGate!.Evaluate(call.Name, call.Arguments as IReadOnlyDictionary<string, object?>))
            return new ToolCallResult($"Tool '{call.Name}' was blocked by the permission policy.", IsError: true);

        if (!_toolClientMap.TryGetValue(call.Name, out var resolvedTool))
            return new ToolCallResult($"No MCP client registered for tool '{call.Name}'.", IsError: true);

        var callResult = await resolvedTool.Client.CallToolAsync(resolvedTool.RawToolName, call.Arguments, cancellationToken: ct);
        var resultText = string.Join("\n",
            callResult.Content
                .OfType<ModelContextProtocol.Protocol.TextContentBlock>()
                .Select(t => t.Text));

        return new ToolCallResult(resultText, callResult.IsError ?? false);
    }

    private IAgentBackend CreateBackend(AgentPersona persona, PermissionGate permissionGate)
    {
        if (_backendFactory is not null)
            return _backendFactory(persona, permissionGate);

        return persona.Backend switch
        {
            "anthropic" => CreateAnthropicBackend(persona.Model),
            _ => throw new InvalidOperationException(
                $"Unknown backend '{persona.Backend}'. Register a backendFactory to support it."),
        };
    }

    private static AnthropicBackend CreateAnthropicBackend(string model)
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("ANTHROPIC_API_KEY environment variable is not set.");

        var anthropic = new AnthropicClient(new Anthropic.Core.ClientOptions { ApiKey = apiKey });
        return new AnthropicBackend(anthropic, string.IsNullOrWhiteSpace(model) ? "claude-haiku-4-5-20251001" : model);
    }

    private static string CreateToolName(string mcpSlug, string rawToolName) => $"{mcpSlug}.{rawToolName}";

    public async ValueTask DisposeAsync()
    {
        if (_backend is not null)
            await _backend.DisposeAsync();

        foreach (var client in _mcpClients)
            await client.DisposeAsync();
    }
}
