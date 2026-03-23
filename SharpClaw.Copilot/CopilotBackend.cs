using System.Text.Json;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using SharpClaw.Core;
using ChatMessage = SharpClaw.Core.ChatMessage;
using ChatRole = SharpClaw.Core.ChatRole;

namespace SharpClaw.Copilot;

/// <summary>
/// <see cref="IAgentBackend"/> implementation that uses the GitHub Copilot SDK.
/// The SDK manages the internal tool-use loop; tool calls are dispatched through
/// the caller-supplied dispatcher.
/// </summary>
public sealed class CopilotBackend : IAgentBackend
{
    private readonly PermissionGate? _permissionGate;
    private CopilotClient? _client;

    public CopilotBackend(PermissionGate? permissionGate = null)
    {
        _permissionGate = permissionGate;
    }

    public async Task<string> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<ToolSchema> tools,
        IReadOnlyList<ChatMessage> history,
        Func<ToolCall, CancellationToken, Task<ToolCallResult>> toolDispatcher,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        _client ??= new CopilotClient(new CopilotClientOptions());

        if (_client.State != ConnectionState.Connected)
        {
            onProgress?.Invoke("Connecting to Copilot…");
            await _client.StartAsync(cancellationToken);
        }

        // Wrap each tool schema as an AIFunction so the SDK can advertise and invoke them.
        var aiFunctions = tools
            .Select(t => (AIFunction)new DispatchingAIFunction(t, toolDispatcher))
            .ToList();

        var session = await _client.CreateSessionAsync(new SessionConfig
        {
            OnPermissionRequest = CreatePermissionHandler(),
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Replace,
                Content = systemPrompt,
            },
            Tools = aiFunctions,
        }, cancellationToken);

        await using (session)
        {
            // Use the last user message from history.
            var lastUserMessage = "";
            for (var i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].Role == ChatRole.User)
                {
                    lastUserMessage = history[i].Content;
                    break;
                }
            }

            onProgress?.Invoke("Thinking…");

            var result = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = lastUserMessage },
                timeout: null,
                cancellationToken: cancellationToken);

            return result?.Data?.Content ?? string.Empty;
        }
    }

    private PermissionRequestHandler CreatePermissionHandler()
    {
        if (_permissionGate is null)
            return PermissionHandler.ApproveAll;

        return (PermissionRequest request, PermissionInvocation invocation) =>
        {
            var (toolName, args) = ExtractToolInfo(request);
            var allowed = _permissionGate.Evaluate(toolName, args);

            return Task.FromResult(new PermissionRequestResult
            {
                Kind = allowed
                    ? PermissionRequestResultKind.Approved
                    : PermissionRequestResultKind.DeniedByRules,
            });
        };
    }

    private static (string ToolName, IReadOnlyDictionary<string, object?>? Args) ExtractToolInfo(
        PermissionRequest request)
    {
        return request switch
        {
            PermissionRequestMcp mcp => (mcp.ToolName ?? mcp.ServerName ?? "mcp_unknown",
                ParseArgs(mcp.Args as string)),
            PermissionRequestCustomTool custom => (custom.ToolName ?? "custom_tool",
                ParseArgs(custom.Args as string)),
            PermissionRequestWrite write => ("write_file",
                new Dictionary<string, object?> { ["fileName"] = write.FileName }),
            PermissionRequestRead read => ("read_file",
                new Dictionary<string, object?> { ["path"] = read.Path }),
            PermissionRequestShell shell => ("run_command",
                new Dictionary<string, object?> { ["command"] = shell.FullCommandText }),
            _ => (request.Kind ?? "unknown", null),
        };
    }

    private static IReadOnlyDictionary<string, object?>? ParseArgs(string? argsJson)
    {
        if (string.IsNullOrEmpty(argsJson))
            return null;

        try
        {
            var doc = JsonDocument.Parse(argsJson);
            return doc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => (object?)p.Value.ToString());
        }
        catch
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
    }
}

/// <summary>
/// An <see cref="AIFunction"/> that delegates execution to an external tool dispatcher.
/// The Copilot SDK calls <see cref="InvokeCoreAsync"/> when the model requests a tool call;
/// we forward it through the SharpClaw permission gate / MCP routing layer.
/// </summary>
internal sealed class DispatchingAIFunction : AIFunction
{
    private readonly ToolSchema _schema;
    private readonly Func<ToolCall, CancellationToken, Task<ToolCallResult>> _dispatcher;

    public DispatchingAIFunction(
        ToolSchema schema,
        Func<ToolCall, CancellationToken, Task<ToolCallResult>> dispatcher)
    {
        _schema = schema;
        _dispatcher = dispatcher;
    }

    public override string Name => _schema.Name;
    public override string Description => _schema.Description ?? string.Empty;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        // Convert AIFunctionArguments → IReadOnlyDictionary<string, object?>
        var args = arguments.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
            as IReadOnlyDictionary<string, object?>;

        var call = new ToolCall(_schema.Name, args);
        var result = await _dispatcher(call, cancellationToken);

        if (result.IsError)
            return $"Error: {result.Content}";

        return result.Content;
    }
}
