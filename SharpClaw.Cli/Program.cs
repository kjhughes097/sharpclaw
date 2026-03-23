using Anthropic;
using ModelContextProtocol.Client;
using SharpClaw.Core;

// ── Configuration ────────────────────────────────────────────────────────────

var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("Error: ANTHROPIC_API_KEY environment variable is not set.");
    return 1;
}

// ── Connect to the MCP filesystem server ─────────────────────────────────────
// The server is launched as a child process via npx.
// We expose /tmp so Claude can list its contents.

Console.WriteLine("Connecting to MCP filesystem server…");

var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Command = "npx",
    Arguments = ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"],
    Name = "filesystem",
});

await using var mcpClient = await McpClient.CreateAsync(transport);

// ── Discover tools ───────────────────────────────────────────────────────────

var mcpTools = await mcpClient.ListToolsAsync();
Console.WriteLine($"Discovered {mcpTools.Count} MCP tool(s):");
foreach (var t in mcpTools)
    Console.WriteLine($"  • {t.Name} — {t.Description}");

var anthropicTools = mcpTools.Select(MessageLoop.ToAnthropicTool).ToList();

// ── Run the agent loop ───────────────────────────────────────────────────────

using var anthropic = new AnthropicClient(new Anthropic.Core.ClientOptions { ApiKey = apiKey });
var loop = new MessageLoop(anthropic);

const string SystemPrompt =
    "You are a helpful assistant with access to the local filesystem. " +
    "When asked about files, use the available tools to retrieve real information.";

const string UserPrompt = "List the files in /tmp";

Console.WriteLine($"\nUser: {UserPrompt}\n");

var answer = await loop.RunAsync(
    systemPrompt: SystemPrompt,
    tools: anthropicTools,
    userMessage: UserPrompt,
    mcpClient: mcpClient);

Console.WriteLine($"Assistant: {answer}");

return 0;
