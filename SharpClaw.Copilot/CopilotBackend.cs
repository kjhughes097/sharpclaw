using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
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
    private readonly string? _workingDirectory;
    private CopilotClient? _client;

    public CopilotBackend(PermissionGate? permissionGate = null, string? workingDirectory = null)
    {
        _permissionGate = permissionGate;
        _workingDirectory = workingDirectory;
    }

    public async Task<string> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<ToolSchema> tools,
        IReadOnlyList<ChatMessage> history,
        Func<ToolCall, CancellationToken, Task<ToolCallResult>> toolDispatcher,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        _client ??= CreateClient();

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
                Mode = SystemMessageMode.Append,
                Content = systemPrompt,
            },
            AvailableTools = new List<string>(),
            Tools = aiFunctions,
            WorkingDirectory = _workingDirectory,
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
                        : PermissionRequestResultKind.DeniedCouldNotRequestFromUser,
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

    /// <summary>
    /// Appends an instruction block listing the available MCP tools so the model
    /// uses them instead of falling back to built-in tools like bash.
    /// </summary>
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

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        string systemPrompt,
        IReadOnlyList<ToolSchema> tools,
        IReadOnlyList<ChatMessage> history,
        Func<ToolCall, CancellationToken, Task<ToolCallResult>> toolDispatcher,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _client ??= CreateClient();

        if (_client.State != ConnectionState.Connected)
            await _client.StartAsync(cancellationToken);

        var aiFunctions = tools
            .Select(t => (AIFunction)new DispatchingAIFunction(t, toolDispatcher))
            .ToList();

        var session = await _client.CreateSessionAsync(new SessionConfig
        {
            OnPermissionRequest = CreatePermissionHandler(),
            Streaming = true,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = systemPrompt,
            },
            AvailableTools = new List<string>(),
            Tools = aiFunctions,
            WorkingDirectory = _workingDirectory,
        }, cancellationToken);

        await using (session)
        {
            var lastUserMessage = "";
            for (var i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].Role == ChatRole.User)
                {
                    lastUserMessage = history[i].Content;
                    break;
                }
            }

            var channel = Channel.CreateUnbounded<AgentEvent>();
            // Track tool call IDs → tool names for pairing with complete events.
            var toolNames = new Dictionary<string, string>();

            // Subscribe to SDK events for intermediate streaming; the On()
            // handler fires on a background thread even while SendAndWaitAsync
            // blocks.  We intentionally do NOT complete the channel here —
            // AssistantTurnEndEvent fires after every internal turn (including
            // mid-tool-loop turns), so using it as a completion signal would
            // close the channel before the final answer arrives.
            using var subscription = session.On(evt =>
            {
                switch (evt)
                {
                    case AssistantIntentEvent intent when !string.IsNullOrEmpty(intent.Data?.Intent):
                        channel.Writer.TryWrite(new StatusEvent(intent.Data!.Intent));
                        break;

                    case AssistantMessageDeltaEvent delta when !string.IsNullOrEmpty(delta.Data?.DeltaContent):
                        channel.Writer.TryWrite(new TokenEvent(delta.Data!.DeltaContent));
                        break;

                    case ToolExecutionStartEvent toolStart:
                        {
                            var name = toolStart.Data?.ToolName ?? toolStart.Data?.McpToolName ?? "unknown";
                            if (toolStart.Data?.ToolCallId is { } id)
                                toolNames[id] = name;

                            IReadOnlyDictionary<string, object?>? args = null;
                            if (toolStart.Data?.Arguments is string argsJson)
                                args = ParseArgs(argsJson);

                            channel.Writer.TryWrite(new ToolCallEvent(name, args));
                            break;
                        }

                    case ToolExecutionProgressEvent progress when !string.IsNullOrEmpty(progress.Data?.ProgressMessage):
                        channel.Writer.TryWrite(new StatusEvent(progress.Data!.ProgressMessage));
                        break;

                    case ToolExecutionCompleteEvent toolComplete:
                        {
                            var toolName = "unknown";
                            if (toolComplete.Data?.ToolCallId is { } id)
                                toolNames.TryGetValue(id, out toolName!);

                            string resultText;
                            if (toolComplete.Data?.Success == true)
                            {
                                resultText = toolComplete.Data.Result?.Content ?? "";
                            }
                            else
                            {
                                var error = toolComplete.Data?.Error;
                                var parts = new List<string>();
                                if (!string.IsNullOrEmpty(error?.Code))
                                    parts.Add($"[{error!.Code}]");
                                if (!string.IsNullOrEmpty(error?.Message))
                                    parts.Add(error!.Message);
                                resultText = parts.Count > 0
                                    ? string.Join(" ", parts)
                                    : "Tool execution failed";
                            }

                            channel.Writer.TryWrite(new ToolResultEvent(
                                toolName,
                                resultText,
                                !(toolComplete.Data?.Success ?? true)));
                            break;
                        }
                }
            });

            // Run SendAndWaitAsync on a background task so the channel
            // reader below can yield events to the caller in real-time.
            // If we awaited it inline, the async iterator would block until
            // the entire exchange finished, buffering all events invisibly.
            var sendTask = Task.Run(async () =>
            {
                try
                {
                    var result = await session.SendAndWaitAsync(
                        new MessageOptions { Prompt = lastUserMessage },
                        timeout: TimeSpan.FromMinutes(10),
                        cancellationToken: cancellationToken);

                    var finalContent = result?.Data?.Content ?? "";
                    channel.Writer.TryWrite(new DoneEvent(finalContent));
                }
                catch (Exception ex)
                {
                    channel.Writer.TryWrite(new DoneEvent($"Error: {ex.Message}"));
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            }, cancellationToken);

            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return evt;
            }

            await sendTask;
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

    private CopilotClient CreateClient()
    {
        var opts = new CopilotClientOptions();

        if (!string.IsNullOrEmpty(_workingDirectory))
            opts.Cwd = _workingDirectory;

        opts.GitHubToken = BackendProviderUtilities.GetRequiredEnvironmentVariable(CopilotBackendProvider.GitHubTokenEnvVar);

        return new CopilotClient(opts);
    }
}

/// <summary>
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
    public override JsonElement JsonSchema => _schema.InputSchema;

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
