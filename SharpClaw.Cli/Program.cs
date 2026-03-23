using Anthropic;
using ModelContextProtocol.Client;
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

// ── Configuration ────────────────────────────────────────────────────────────

var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("Error: ANTHROPIC_API_KEY environment variable is not set.");
    return 1;
}

// ── Load persona ─────────────────────────────────────────────────────────────

var persona = AgentPersonaLoader.Load(agentFilePath);
Console.WriteLine($"Loaded agent: {persona.Name}");
Console.WriteLine($"MCP servers:  {string.Join(", ", persona.McpServers)}");

// ── Connect to MCP servers listed in the persona ─────────────────────────────

var mcpClients = new List<McpClient>();
var allTools = new List<Anthropic.Models.Messages.ToolUnion>();

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
            allTools.Add(MessageLoop.ToAnthropicTool(t));
            toolClientMap[t.Name] = client;
        }
    }

    // ── Build permission gate ─────────────────────────────────────────────────

    var permissionGate = new PermissionGate(persona.PermissionPolicy);

    // ── Run the agent loop ───────────────────────────────────────────────────

    using var anthropic = new AnthropicClient(new Anthropic.Core.ClientOptions { ApiKey = apiKey });
    var loop = new MessageLoop(anthropic);

    Console.WriteLine($"\nUser: {userPrompt}\n");

    var answer = await loop.RunAsync(
        systemPrompt: persona.SystemPrompt,
        tools: allTools,
        userMessage: userPrompt,
        toolClientMap: toolClientMap,
        permissionGate: permissionGate);

    Console.WriteLine($"Assistant: {answer}");
    return 0;
}
finally
{
    foreach (var client in mcpClients)
        await client.DisposeAsync();
}
