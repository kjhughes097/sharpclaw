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
    private readonly AgentPersona _persona;
    private readonly List<McpClient> _mcpClients = [];
    private readonly List<ToolSchema> _toolSchemas = [];
    private readonly Dictionary<string, McpClient> _toolClientMap = [];
    private PermissionGate? _permissionGate;
    private IAgentBackend? _backend;
    private bool _initialized;

    public AgentRunner(AgentPersona persona)
    {
        _persona = persona;
    }

    public AgentPersona Persona => _persona;
    public IReadOnlyList<ToolSchema> Tools => _toolSchemas;

    /// <summary>
    /// Connects MCP servers, builds the permission gate, and creates the backend.
    /// Must be called before <see cref="SendAsync"/>.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        foreach (var serverName in _persona.McpServers)
        {
            var transport = McpServerRegistry.Resolve(serverName);
            var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
            _mcpClients.Add(client);

            var tools = await client.ListToolsAsync(cancellationToken: ct);
            foreach (var t in tools)
            {
                _toolSchemas.Add(t.ToToolSchema());
                _toolClientMap[t.Name] = client;
            }
        }

        _permissionGate = new PermissionGate(_persona.PermissionPolicy);
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

    private async Task<ToolCallResult> ToolDispatcher(ToolCall call, CancellationToken ct)
    {
        if (!_permissionGate!.Evaluate(call.Name, call.Arguments as IReadOnlyDictionary<string, object?>))
            return new ToolCallResult($"Tool '{call.Name}' was blocked by the permission policy.", IsError: true);

        if (!_toolClientMap.TryGetValue(call.Name, out var mcpClient))
            return new ToolCallResult($"No MCP client registered for tool '{call.Name}'.", IsError: true);

        var callResult = await mcpClient.CallToolAsync(call.Name, call.Arguments, cancellationToken: ct);
        var resultText = string.Join("\n",
            callResult.Content
                .OfType<ModelContextProtocol.Protocol.TextContentBlock>()
                .Select(t => t.Text));

        return new ToolCallResult(resultText, callResult.IsError ?? false);
    }

    private static IAgentBackend CreateBackend(AgentPersona persona, PermissionGate permissionGate)
    {
        return persona.Backend switch
        {
            "anthropic" => CreateAnthropicBackend(),
            // Copilot backend requires the SharpClaw.Copilot assembly — if it's not
            // referenced, fall through to the error. The API project can add its own
            // backend factory via the overload.
            _ => throw new InvalidOperationException(
                $"Unknown backend '{persona.Backend}'. Supported: anthropic."),
        };
    }

    private static AnthropicBackend CreateAnthropicBackend()
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("ANTHROPIC_API_KEY environment variable is not set.");

        var anthropic = new AnthropicClient(new Anthropic.Core.ClientOptions { ApiKey = apiKey });
        return new AnthropicBackend(anthropic);
    }

    public async ValueTask DisposeAsync()
    {
        if (_backend is not null)
            await _backend.DisposeAsync();

        foreach (var client in _mcpClients)
            await client.DisposeAsync();
    }
}
