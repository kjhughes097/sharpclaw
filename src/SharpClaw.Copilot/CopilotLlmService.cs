using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using SharpClaw.Core;

using CoreChatMessage = SharpClaw.Core.ChatMessage;
using CoreChatRole = SharpClaw.Core.ChatRole;
using CoreToolCall = SharpClaw.Core.ToolCall;

namespace SharpClaw.Copilot;

/// <summary>
/// LLM service that delegates to the GitHub Copilot SDK.
/// The SDK spawns the Copilot CLI and manages tool execution internally.
/// </summary>
public sealed class CopilotLlmService : ILlmService, IAsyncDisposable
{
    private readonly CopilotClient _client;
    private bool _started;

    public string ServiceName => "copilot";

    public CopilotLlmService(string? githubToken = null)
    {
        var opts = new CopilotClientOptions { AutoStart = false };
        if (githubToken is not null)
            opts.GitHubToken = githubToken;

        _client = new CopilotClient(opts);
    }

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        string model,
        string systemPrompt,
        IReadOnlyList<CoreChatMessage> history,
        IReadOnlyList<ToolSchema> tools,
        Func<CoreToolCall, CancellationToken, Task<ToolCallResult>> toolDispatcher,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsureStartedAsync(ct);

        // Convert our tool schemas into AIFunction instances for the Copilot SDK
        var sdkTools = tools.Select(t => CreateAIFunction(t, toolDispatcher, ct)).ToList();

        // Create a streaming session
        await using var session = await _client.CreateSessionAsync(new SessionConfig
        {
            Model = model,
            Streaming = true,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Replace,
                Content = BuildFullPrompt(systemPrompt, history),
            },
            Tools = sdkTools,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
        });

        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = true,
        });

        var fullContent = new StringBuilder();

        using var subscription = session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent delta:
                    var text = delta.Data.DeltaContent ?? "";
                    fullContent.Append(text);
                    channel.Writer.TryWrite(new TokenEvent(text));
                    break;

                case ToolExecutionStartEvent toolStart:
                    channel.Writer.TryWrite(new ToolCallEvent(
                        toolStart.Data.ToolName ?? "unknown",
                        toolStart.Data.Arguments?.ToString()));
                    break;

                case ToolExecutionCompleteEvent toolComplete:
                    channel.Writer.TryWrite(new ToolResultEvent(
                        toolComplete.Data.ToolCallId ?? "unknown",
                        toolComplete.Data.Result?.ToString() ?? toolComplete.Data.Error?.ToString() ?? "",
                        toolComplete.Data.Success != true));
                    break;

                case AssistantMessageEvent msg:
                    // Final message — don't duplicate content already streamed via deltas
                    break;

                case SessionIdleEvent:
                    channel.Writer.TryWrite(new DoneEvent(fullContent.ToString()));
                    channel.Writer.TryComplete();
                    break;

                case SessionErrorEvent err:
                    channel.Writer.TryWrite(new StatusEvent($"Error: {err.Data.Message}"));
                    channel.Writer.TryWrite(new DoneEvent(fullContent.ToString()));
                    channel.Writer.TryComplete();
                    break;
            }
        });

        // Extract just the latest user message from history
        var latestUserMessage = history.LastOrDefault(m => m.Role == CoreChatRole.User)?.Content ?? "";

        await session.SendAsync(new MessageOptions { Prompt = latestUserMessage });

        await foreach (var evt in channel.Reader.ReadAllAsync(ct))
        {
            yield return evt;
        }
    }

    private static string BuildFullPrompt(string systemPrompt, IReadOnlyList<CoreChatMessage> history)
    {
        // Include conversation history in the system prompt since the Copilot SDK
        // manages its own session state and we create a fresh session per turn.
        if (history.Count <= 1) // Only or no user message — no history to inject
            return systemPrompt;

        var sb = new StringBuilder(systemPrompt);
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("<conversation_history>");
        // Include all messages except the last user message (which is sent via SendAsync)
        foreach (var msg in history.SkipLast(1))
        {
            var role = msg.Role == CoreChatRole.Assistant ? "assistant" : "user";
            sb.AppendLine($"<{role}>{msg.Content}</{role}>");
        }
        sb.AppendLine("</conversation_history>");
        return sb.ToString();
    }

    private static AIFunction CreateAIFunction(
        ToolSchema schema,
        Func<CoreToolCall, CancellationToken, Task<ToolCallResult>> dispatcher,
        CancellationToken ct)
    {
        return AIFunctionFactory.Create(
            async (string argsJson) =>
            {
                var result = await dispatcher(new CoreToolCall(schema.Name, argsJson), ct);
                return result.Content;
            },
            schema.Name,
            schema.Description);
    }

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_started) return;
        await _client.StartAsync();
        _started = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_started)
            await _client.StopAsync();
        await _client.DisposeAsync();
    }
}
