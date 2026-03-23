using Anthropic;
using ModelContextProtocol.Client;
using SharpClaw.Copilot;
using SharpClaw.Core;

// ── Parse CLI arguments ──────────────────────────────────────────────────────

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: SharpClaw.Cli <path-to-agent.md> [user-prompt]");
    return 1;
}

var agentFilePath = args[0];
if (!File.Exists(agentFilePath))
{
    Console.Error.WriteLine($"Error: Agent file not found: {agentFilePath}");
    return 1;
}

var userPrompt = args.Length >= 2 ? args[1] : "Hello";

// ── Load persona ─────────────────────────────────────────────────────────────

var persona = AgentPersonaLoader.Load(agentFilePath);
Console.WriteLine($"Loaded agent: {persona.Name} (backend: {persona.Backend})");
Console.WriteLine($"MCP servers:  {string.Join(", ", persona.McpServers)}");

// ── Connect to MCP servers listed in the persona ─────────────────────────────

var mcpClients = new List<McpClient>();
var toolSchemas = new List<ToolSchema>();

// Map from tool name → which MCP client owns it, for dispatching calls.
var toolClientMap = new Dictionary<string, McpClient>();

try
{
    foreach (var serverName in persona.McpServers)
    {
        Console.WriteLine($"Connecting to MCP server '{serverName}'…");
        var transport = McpServerRegistry.Resolve(serverName);
        var client = await McpClient.CreateAsync(transport);
        mcpClients.Add(client);

        var tools = await client.ListToolsAsync();
        Console.WriteLine($"  Discovered {tools.Count} tool(s):");
        foreach (var t in tools)
        {
            Console.WriteLine($"    • {t.Name} — {t.Description}");
            toolSchemas.Add(t.ToToolSchema());
            toolClientMap[t.Name] = client;
        }
    }

    // ── Build permission gate ─────────────────────────────────────────────────

    var permissionGate = new PermissionGate(persona.PermissionPolicy);

    // ── Build tool dispatcher (MCP routing + permission gate) ─────────────────

    async Task<ToolCallResult> ToolDispatcher(ToolCall call, CancellationToken ct)
    {
        // Check permission.
        if (!permissionGate.Evaluate(call.Name, call.Arguments as IReadOnlyDictionary<string, object?>))
            return new ToolCallResult($"Tool '{call.Name}' was blocked by the permission policy.", IsError: true);

        // Route to the correct MCP client.
        if (!toolClientMap.TryGetValue(call.Name, out var mcpClient))
            return new ToolCallResult($"No MCP client registered for tool '{call.Name}'.", IsError: true);

        var callResult = await mcpClient.CallToolAsync(call.Name, call.Arguments, cancellationToken: ct);
        var resultText = string.Join("\n",
            callResult.Content
                .OfType<ModelContextProtocol.Protocol.TextContentBlock>()
                .Select(t => t.Text));

        return new ToolCallResult(resultText, callResult.IsError ?? false);
    }

    // ── Instantiate backend ──────────────────────────────────────────────────

    IAgentBackend backend = persona.Backend switch
    {
        "anthropic" => CreateAnthropicBackend(),
        "copilot" => new CopilotBackend(permissionGate),
        _ => throw new InvalidOperationException(
            $"Unknown backend '{persona.Backend}'. Supported: anthropic, copilot."),
    };

    await using (backend)
    {
        var history = new List<ChatMessage> { new(ChatRole.User, userPrompt) };

        Console.WriteLine($"\nUser: {userPrompt}\n");

        var answer = await backend.CompleteAsync(
            systemPrompt: persona.SystemPrompt,
            tools: toolSchemas,
            history: history,
            toolDispatcher: ToolDispatcher);

        Console.WriteLine($"Assistant: {answer}");
    }

    return 0;
}
finally
{
    foreach (var client in mcpClients)
        await client.DisposeAsync();
}

// ── Helpers ──────────────────────────────────────────────────────────────────

AnthropicBackend CreateAnthropicBackend()
{
    var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
        throw new InvalidOperationException("ANTHROPIC_API_KEY environment variable is not set.");

    var anthropic = new AnthropicClient(new Anthropic.Core.ClientOptions { ApiKey = apiKey });
    return new AnthropicBackend(anthropic);
}
