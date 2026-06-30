using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using SharpClaw.Models;

namespace SharpClaw.Execution;

/// <summary>
/// A lightweight activation tool that defers MCP server connection until first use.
/// On invocation, connects to the MCP server, discovers its tools, and injects them
/// into the session's tool list — saving input tokens on every request until needed.
/// </summary>
public sealed class LazyMcpActivationTool : AIFunction, IAsyncDisposable
{
    private readonly string _serverName;
    private readonly McpServerDefinition _definition;
    private readonly ILoggerFactory _loggerFactory;
    private readonly List<AITool> _sessionTools;
    private readonly ILogger _logger;
    private McpClient? _client;
    private bool _activated;

    private static readonly JsonElement EmptySchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { },
        required = Array.Empty<string>()
    });

    public LazyMcpActivationTool(
        string serverName,
        McpServerDefinition definition,
        ILoggerFactory loggerFactory,
        List<AITool> sessionTools)
    {
        _serverName = serverName;
        _definition = definition;
        _loggerFactory = loggerFactory;
        _sessionTools = sessionTools;
        _logger = loggerFactory.CreateLogger<LazyMcpActivationTool>();
    }

    public override string Name => $"activate_{_serverName}";

    public override string Description =>
        $"Activate the {_serverName} toolset. Call this before using any {_serverName} tools. " +
        $"Once activated, the tools will be available for the rest of the conversation.";

    public override JsonElement JsonSchema => EmptySchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (_activated)
            return $"The {_serverName} tools are already activated.";

        try
        {
            IClientTransport transport = _definition.Transport.ToLowerInvariant() switch
            {
                "http" => new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(_definition.Url
                        ?? throw new InvalidOperationException($"MCP server '{_serverName}' has HTTP transport but no URL")),
                }, _loggerFactory),
                "stdio" => new StdioClientTransport(new StdioClientTransportOptions
                {
                    Command = _definition.Command
                        ?? throw new InvalidOperationException($"MCP server '{_serverName}' has stdio transport but no command"),
                    Arguments = _definition.Args?.ToList(),
                    EnvironmentVariables = _definition.Env?.ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value),
                }, _loggerFactory),
                _ => throw new InvalidOperationException($"Unknown MCP transport '{_definition.Transport}' for server '{_serverName}'")
            };

            _client = await McpClient.CreateAsync(transport, McpToolBridge.DefaultClientOptions, loggerFactory: _loggerFactory, cancellationToken: cancellationToken);
            var tools = await _client.ListToolsAsync(cancellationToken: cancellationToken);

            // Inject discovered tools into the session's live tool list.
            // Anthropic rejects duplicate tool names, so skip any name already present.
            var existingNames = new HashSet<string>(_sessionTools.Select(t => t.Name), StringComparer.Ordinal);
            var added = new List<McpClientTool>();
            foreach (var tool in tools)
            {
                if (existingNames.Add(tool.Name))
                {
                    _sessionTools.Add(tool);
                    added.Add(tool);
                }
                else
                {
                    _logger.LogWarning(
                        "Lazy MCP '{Server}' exposes tool '{Tool}' that is already registered — skipping to avoid duplicate tool name",
                        _serverName, tool.Name);
                }
            }

            _activated = true;

            var toolNames = added.Select(t => t.Name).OrderBy(n => n).ToList();
            _logger.LogInformation(
                "Lazy MCP '{Server}' activated: {Count} tools discovered ({Tools})",
                _serverName, toolNames.Count, string.Join(", ", toolNames));

            return $"Activated {_serverName} — {toolNames.Count} tools now available: {string.Join(", ", toolNames)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to activate lazy MCP server '{Server}'", _serverName);
            return $"Error activating {_serverName}: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
    }
}
