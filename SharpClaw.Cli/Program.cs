using Anthropic;
using ModelContextProtocol.Client;
using SharpClaw.Copilot;
using SharpClaw.Core;

// ── Parse CLI arguments ──────────────────────────────────────────────────────

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: SharpClaw.Cli [path-to-agent.md] <user-prompt>");
    Console.Error.WriteLine("  If path-to-agent.md is omitted, the coordinator routes to the best specialist.");
    return 1;
}

// Detect whether the first arg is an agent file or a prompt.
// If it ends with .agent.md and exists on disk, treat it as a direct agent invocation.
string? explicitAgentFile = null;
string userPrompt;

if (args[0].EndsWith(".agent.md", StringComparison.OrdinalIgnoreCase) && File.Exists(args[0]))
{
    explicitAgentFile = args[0];
    userPrompt = args.Length >= 2 ? args[1] : "Hello";
}
else
{
    // No agent file — everything is the prompt; coordinator will route.
    userPrompt = string.Join(" ", args);
}

// ── Resolve the agents directory (sibling to the CLI project) ────────────────

var agentsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "agents");
agentsDir = Path.GetFullPath(agentsDir);
if (!Directory.Exists(agentsDir))
{
    // Fallback: look relative to the current working directory.
    agentsDir = Path.GetFullPath("agents");
}

// ── Coordinator routing (when no explicit agent is specified) ─────────────────

if (explicitAgentFile is null)
{
    var coordinatorFile = Path.Combine(agentsDir, "coordinator.agent.md");
    if (!File.Exists(coordinatorFile))
    {
        Console.Error.WriteLine($"Error: Coordinator agent not found at {coordinatorFile}");
        return 1;
    }

    // Discover specialist agents (everything in agents/ except coordinator itself).
    var availableAgents = new Dictionary<string, string>();
    foreach (var file in Directory.GetFiles(agentsDir, "*.agent.md"))
    {
        var filename = Path.GetFileName(file);
        if (filename.Equals("coordinator.agent.md", StringComparison.OrdinalIgnoreCase))
            continue;

        var specialist = AgentPersonaLoader.Load(file);
        availableAgents[filename] = specialist.Name + " — " + specialist.SystemPrompt.Split('\n')[0];
    }

    Console.WriteLine($"Coordinator sees {availableAgents.Count} specialist(s):");
    foreach (var (file, desc) in availableAgents)
        Console.WriteLine($"  • {file}: {desc}");

    // Run the coordinator (single-turn, no tools).
    var coordinatorPersona = AgentPersonaLoader.Load(coordinatorFile);
    await using var coordinatorBackend = CreateBackend(coordinatorPersona, permissionGate: null);
    var coordinator = new CoordinatorAgent(coordinatorBackend, coordinatorPersona);

    Console.WriteLine($"\nUser: {userPrompt}");
    Console.WriteLine("Routing…\n");

    var decision = await coordinator.RouteAsync(userPrompt, availableAgents);

    if (decision.Agent is null)
    {
        Console.Error.WriteLine("Coordinator could not find a suitable specialist for this request.");
        return 1;
    }

    Console.WriteLine($"→ Routed to: {decision.Agent}");
    Console.WriteLine($"→ Rewritten: {decision.RewrittenPrompt}\n");

    explicitAgentFile = Path.Combine(agentsDir, decision.Agent);
    userPrompt = decision.RewrittenPrompt ?? userPrompt;

    if (!File.Exists(explicitAgentFile))
    {
        Console.Error.WriteLine($"Error: Routed agent file not found: {explicitAgentFile}");
        return 1;
    }
}

// ── Load the specialist persona ──────────────────────────────────────────────

var persona = AgentPersonaLoader.Load(explicitAgentFile);
Console.WriteLine($"Loaded agent: {persona.Name} (backend: {persona.Backend})");
Console.WriteLine($"MCP servers:  {string.Join(", ", persona.McpServers)}");

// ── Connect to MCP servers listed in the persona ─────────────────────────────

var mcpClients = new List<McpClient>();
var toolSchemas = new List<ToolSchema>();
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
        if (!permissionGate.Evaluate(call.Name, call.Arguments as IReadOnlyDictionary<string, object?>))
            return new ToolCallResult($"Tool '{call.Name}' was blocked by the permission policy.", IsError: true);

        if (!toolClientMap.TryGetValue(call.Name, out var mcpClient))
            return new ToolCallResult($"No MCP client registered for tool '{call.Name}'.", IsError: true);

        var callResult = await mcpClient.CallToolAsync(call.Name, call.Arguments, cancellationToken: ct);
        var resultText = string.Join("\n",
            callResult.Content
                .OfType<ModelContextProtocol.Protocol.TextContentBlock>()
                .Select(t => t.Text));

        return new ToolCallResult(resultText, callResult.IsError ?? false);
    }

    // ── Instantiate backend and run ──────────────────────────────────────────

    await using var backend = CreateBackend(persona, permissionGate);

    var history = new List<ChatMessage> { new(ChatRole.User, userPrompt) };
    Console.WriteLine($"\nUser: {userPrompt}\n");

    var answer = await backend.CompleteAsync(
        systemPrompt: persona.SystemPrompt,
        tools: toolSchemas,
        history: history,
        toolDispatcher: ToolDispatcher);

    Console.WriteLine($"Assistant: {answer}");
    return 0;
}
finally
{
    foreach (var client in mcpClients)
        await client.DisposeAsync();
}

// ── Helpers ──────────────────────────────────────────────────────────────────

IAgentBackend CreateBackend(AgentPersona p, PermissionGate? permissionGate) => p.Backend switch
{
    "anthropic" => CreateAnthropicBackend(),
    "copilot" => new CopilotBackend(permissionGate),
    _ => throw new InvalidOperationException(
        $"Unknown backend '{p.Backend}'. Supported: anthropic, copilot."),
};

AnthropicBackend CreateAnthropicBackend()
{
    var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
        throw new InvalidOperationException("ANTHROPIC_API_KEY environment variable is not set.");

    var anthropic = new AnthropicClient(new Anthropic.Core.ClientOptions { ApiKey = apiKey });
    return new AnthropicBackend(anthropic);
}
