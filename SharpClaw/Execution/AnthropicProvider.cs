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

        // Bridge eager MCP servers to get their tools
        McpToolBridge? mcpBridge = null;
        var allTools = new List<AITool>();

        // Anthropic rejects requests whose tools array contains duplicate names.
        // Track seen names and keep only the first occurrence of each.
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        void AddDistinct(AITool tool)
        {
            if (seenNames.Add(tool.Name))
            {
                allTools.Add(tool);
            }
            else
            {
                logger.LogWarning(
                    "Skipping duplicate tool '{ToolName}' for Anthropic session — a tool with this name is already registered",
                    tool.Name);
            }
        }

        if (request.McpServers is { Count: > 0 })
        {
            mcpBridge = await McpToolBridge.CreateAsync(request.McpServers, loggerFactory, ct);
            foreach (var tool in mcpBridge.Tools)
                AddDistinct(tool);
        }

        // Add ITool adapters (already wrapped as AIFunction)
        if (request.Tools is { Count: > 0 })
        {
            foreach (var tool in request.Tools)
                AddDistinct(tool);
        }

        // Create activation tools for lazy MCP servers
        var lazyActivationTools = new List<LazyMcpActivationTool>();
        if (request.LazyMcpServers is { Count: > 0 })
        {
            foreach (var (name, def) in request.LazyMcpServers)
            {
                var activationTool = new LazyMcpActivationTool(name, def, loggerFactory, allTools);
                lazyActivationTools.Add(activationTool);
                AddDistinct(activationTool);
            }
        }

        var chatOptions = new ChatOptions
        {
            Tools = allTools.Count > 0 ? allTools : null,
        };

        logger.LogDebug("Created Anthropic session with model {Model}, {ToolCount} tools ({LazyCount} lazy MCPs pending)",
            model, allTools.Count, lazyActivationTools.Count);

        return new AnthropicLlmSession(chatClient, chatOptions, request.SystemPrompt, mcpBridge, lazyActivationTools);
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

            var inputTokens = (int?)response.Usage?.InputTokenCount;
            var outputTokens = (int?)response.Usage?.OutputTokenCount;

            logger.LogDebug("Anthropic response received ({Length} chars, in={Input}, out={Output})",
                content.Length, inputTokens, outputTokens);
            return AgentRunResult.Ok(content, session.SessionId, inputTokens, outputTokens);
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
        private readonly IReadOnlyList<LazyMcpActivationTool> _lazyActivationTools;

        public AnthropicLlmSession(
            IChatClient chatClient,
            ChatOptions chatOptions,
            string? systemPrompt,
            McpToolBridge? mcpBridge,
            IReadOnlyList<LazyMcpActivationTool> lazyActivationTools)
        {
            ChatClient = chatClient;
            ChatOptions = chatOptions;
            _mcpBridge = mcpBridge;
            _lazyActivationTools = lazyActivationTools;

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
            foreach (var tool in _lazyActivationTools)
                await tool.DisposeAsync();

            if (_mcpBridge is not null)
                await _mcpBridge.DisposeAsync();

            if (ChatClient is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }
    }
}
