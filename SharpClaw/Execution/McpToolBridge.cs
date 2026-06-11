using System.Reflection;
using ModelContextProtocol.Client;
using SharpClaw.Models;

namespace SharpClaw.Execution;

public sealed class McpToolBridge : IAsyncDisposable
{
    internal static readonly McpClientOptions DefaultClientOptions = new()
    {
        ClientInfo = new()
        {
            Name = "SharpClaw",
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0"
        }
    };

    private readonly List<McpClient> _clients = [];

    public IReadOnlyList<McpClientTool> Tools { get; private set; } = [];

    public static async Task<McpToolBridge> CreateAsync(
        IReadOnlyDictionary<string, McpServerDefinition> servers,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var bridge = new McpToolBridge();
        var allTools = new List<McpClientTool>();

        foreach (var (name, def) in servers)
        {
            try
            {
                IClientTransport transport = def.Transport.ToLowerInvariant() switch
                {
                    "http" => new HttpClientTransport(new HttpClientTransportOptions
                    {
                        Endpoint = new Uri(def.Url ?? throw new InvalidOperationException($"MCP server '{name}' has HTTP transport but no URL")),
                    }, loggerFactory),
                    "stdio" => new StdioClientTransport(new StdioClientTransportOptions
                    {
                        Command = def.Command ?? throw new InvalidOperationException($"MCP server '{name}' has stdio transport but no command"),
                        Arguments = def.Args?.ToList(),
                        EnvironmentVariables = def.Env?.ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value),
                    }, loggerFactory),
                    _ => throw new InvalidOperationException($"Unknown MCP transport type '{def.Transport}' for server '{name}'")
                };

                var client = await McpClient.CreateAsync(transport, DefaultClientOptions, loggerFactory: loggerFactory, cancellationToken: ct);
                bridge._clients.Add(client);

                var tools = await client.ListToolsAsync(cancellationToken: ct);
                allTools.AddRange(tools);
            }
            catch (Exception ex)
            {
                var logger = loggerFactory.CreateLogger<McpToolBridge>();
                logger.LogWarning(ex, "Failed to connect to MCP server '{Name}' — skipping", name);
            }
        }

        bridge.Tools = allTools;
        return bridge;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync();
        }
        _clients.Clear();
    }
}
