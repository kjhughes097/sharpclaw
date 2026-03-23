using Anthropic;
using ModelContextProtocol.Client;
using SharpClaw.Copilot;
using SharpClaw.Core;

// ── Parse CLI arguments ──────────────────────────────────────────────────────

string? explicitAgentFile = null;
string? sessionId = null;
string? userPrompt = null;

var positional = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--session" && i + 1 < args.Length)
    {
        sessionId = args[++i];
    }
    else
    {
        positional.Add(args[i]);
    }
}

if (positional.Count >= 1 &&
    positional[0].EndsWith(".agent.md", StringComparison.OrdinalIgnoreCase) &&
    File.Exists(positional[0]))
{
    explicitAgentFile = positional[0];
    if (positional.Count >= 2)
        userPrompt = positional[1];
}
else if (positional.Count >= 1)
{
    userPrompt = string.Join(" ", positional);
}

if (userPrompt is null && sessionId is null)
{
    Console.Error.WriteLine("Usage: SharpClaw.Cli [path-to-agent.md] [--session <id>] <user-prompt>");
    Console.Error.WriteLine("  --session <id>  Resume or start a named conversation.");
    Console.Error.WriteLine("  Omit the agent file to let the coordinator route automatically.");
    Console.Error.WriteLine("  Omit the prompt to enter interactive (REPL) mode.");
    return 1;
}

// ── Resolve the agents directory ─────────────────────────────────────────────

var agentsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "agents");
agentsDir = Path.GetFullPath(agentsDir);
if (!Directory.Exists(agentsDir))
    agentsDir = Path.GetFullPath("agents");

// ── Session store ────────────────────────────────────────────────────────────

var dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "sharpclaw", "sessions.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
using var store = new SessionStore(dbPath);

// ── Resume an existing session? ──────────────────────────────────────────────

ConversationHistory? conversation = null;

if (sessionId is not null)
{
    conversation = store.Load(sessionId);
    if (conversation is not null)
    {
        Console.WriteLine($"Resumed session '{sessionId}' ({conversation.Count} messages)");
        explicitAgentFile = conversation.AgentFile;
    }
}

// ── Coordinator routing (when no explicit agent is specified) ─────────────────

if (explicitAgentFile is null)
{
    // Need a prompt to route — in REPL mode with no session, ask for one.
    var routingPrompt = userPrompt;
    if (routingPrompt is null)
    {
        Console.Write("You: ");
        routingPrompt = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(routingPrompt))
            return 0;
        userPrompt = routingPrompt;
    }

    var coordinatorFile = Path.Combine(agentsDir, "coordinator.agent.md");
    if (!File.Exists(coordinatorFile))
    {
        Console.Error.WriteLine($"Error: Coordinator agent not found at {coordinatorFile}");
        return 1;
    }

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

    var coordinatorPersona = AgentPersonaLoader.Load(coordinatorFile);
    await using var coordinatorBackend = CreateBackend(coordinatorPersona, permissionGate: null);
    var coordinator = new CoordinatorAgent(coordinatorBackend, coordinatorPersona);

    Console.WriteLine($"\nUser: {routingPrompt}");
    Console.WriteLine("Routing…\n");

    var decision = await coordinator.RouteAsync(routingPrompt, availableAgents);

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

// ── Initialise session if new ────────────────────────────────────────────────

sessionId ??= Guid.NewGuid().ToString("N")[..8];

if (conversation is null)
{
    conversation = new ConversationHistory(sessionId, explicitAgentFile);
    store.CreateSession(sessionId, explicitAgentFile);
    Console.WriteLine($"Session: {sessionId} (new)");
}

// ── Connect to MCP servers ───────────────────────────────────────────────────

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

    var permissionGate = new PermissionGate(persona.PermissionPolicy);

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

    await using var backend = CreateBackend(persona, permissionGate);

    // ── Conversation loop ────────────────────────────────────────────────────

    // If we already have a prompt from args, use it for the first turn.
    // Then enter REPL mode if --session was given or no prompt remains.
    var interactive = userPrompt is null;
    var firstPrompt = userPrompt;

    while (true)
    {
        string input;
        if (firstPrompt is not null)
        {
            input = firstPrompt;
            firstPrompt = null;
        }
        else
        {
            Console.Write("\nYou: ");
            var line = Console.ReadLine();
            if (line is null || string.Equals(line.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
                break;
            input = line.Trim();
            if (input.Length == 0) continue;
        }

        // Append user turn.
        var userMsg = new ChatMessage(ChatRole.User, input);
        conversation.AddUser(input);
        store.Append(sessionId, userMsg);

        // Truncate history to fit context window.
        var truncated = HistoryTruncator.Truncate(
            conversation.Messages, persona.SystemPrompt);

        Console.WriteLine($"\nUser: {input}\n");

        var answer = await backend.CompleteAsync(
            systemPrompt: persona.SystemPrompt,
            tools: toolSchemas,
            history: truncated,
            toolDispatcher: ToolDispatcher,
            onProgress: msg => Console.Error.WriteLine($"  [{msg}]"));

        Console.WriteLine($"Assistant: {answer}");

        // Append assistant turn.
        var assistantMsg = new ChatMessage(ChatRole.Assistant, answer);
        conversation.AddAssistant(answer);
        store.Append(sessionId, assistantMsg);

        // Single-shot mode: exit after one turn unless interactive.
        if (!interactive && userPrompt is not null)
            break;

        // After the first turn, always go interactive if we have a session.
        interactive = true;
    }

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
