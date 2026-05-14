using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SharpClaw.Abstractions;
using SharpClaw.Configuration;
using SharpClaw.Models;

namespace SharpClaw.Execution;

public sealed class AnthropicProvider(
    IOptions<AnthropicOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<AnthropicProvider> logger) : ILlmProvider
{
    public string ProviderName => "anthropic";

    private readonly AnthropicClient _client = new() { ApiKey = options.Value.ApiKey };

    public async Task<ILlmSession> CreateSessionAsync(LlmSessionRequest request, CancellationToken ct = default)
    {
        var model = request.Model ?? options.Value.DefaultModel;
        var maxTokens = options.Value.MaxTokens;

        IChatClient chatClient = _client
            .AsIChatClient(model, maxTokens)
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();

        // Bridge MCP servers to get their tools
        McpToolBridge? mcpBridge = null;
        var allTools = new List<AITool>();

        if (request.McpServers is { Count: > 0 })
        {
            mcpBridge = await McpToolBridge.CreateAsync(request.McpServers, loggerFactory, ct);
            allTools.AddRange(mcpBridge.Tools);
        }

        // Add ITool adapters (already wrapped as AIFunction)
        if (request.Tools is { Count: > 0 })
        {
            allTools.AddRange(request.Tools);
        }

        var chatOptions = new ChatOptions
        {
            Tools = allTools.Count > 0 ? allTools : null,
        };

        logger.LogDebug("Created Anthropic session with model {Model}, {ToolCount} tools",
            model, allTools.Count);

        return new AnthropicLlmSession(chatClient, chatOptions, request.SystemPrompt, mcpBridge);
    }

    public async Task<AgentRunResult> SendAsync(ILlmSession session, string prompt, CancellationToken ct = default)
    {
        if (session is not AnthropicLlmSession anthropicSession)
            return AgentRunResult.Fail("Invalid session type for Anthropic provider.");

        try
        {
            anthropicSession.AddUserMessage(prompt);

            var response = await anthropicSession.ChatClient.GetResponseAsync(
                anthropicSession.Messages,
                anthropicSession.ChatOptions,
                ct);

            var content = response.Text ?? string.Empty;

            // Add assistant response to history for multi-turn
            anthropicSession.AddAssistantMessage(content);

            logger.LogDebug("Anthropic response received ({Length} chars)", content.Length);
            return AgentRunResult.Ok(content, session.SessionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Anthropic send failed");
            return AgentRunResult.Fail(ex.Message);
        }
    }

    private sealed class AnthropicLlmSession : ILlmSession
    {
        public string SessionId { get; } = Guid.NewGuid().ToString();
        public IChatClient ChatClient { get; }
        public ChatOptions ChatOptions { get; }
        public List<ChatMessage> Messages { get; } = [];
        private readonly McpToolBridge? _mcpBridge;

        public AnthropicLlmSession(
            IChatClient chatClient,
            ChatOptions chatOptions,
            string? systemPrompt,
            McpToolBridge? mcpBridge)
        {
            ChatClient = chatClient;
            ChatOptions = chatOptions;
            _mcpBridge = mcpBridge;

            if (!string.IsNullOrEmpty(systemPrompt))
            {
                Messages.Add(new ChatMessage(ChatRole.System, systemPrompt));
            }
        }

        public void AddUserMessage(string content) =>
            Messages.Add(new ChatMessage(ChatRole.User, content));

        public void AddAssistantMessage(string content) =>
            Messages.Add(new ChatMessage(ChatRole.Assistant, content));

        public async ValueTask DisposeAsync()
        {
            if (_mcpBridge is not null)
                await _mcpBridge.DisposeAsync();

            if (ChatClient is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }
    }
}
