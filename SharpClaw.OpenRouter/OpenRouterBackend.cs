using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using global::OpenAI;
using global::OpenAI.Chat;
using SharpClaw.Core;

using CoreChatMessage = SharpClaw.Core.ChatMessage;
using CoreToolCall = SharpClaw.Core.ToolCall;
using OAIChatMessage = global::OpenAI.Chat.ChatMessage;
using OAIChatTool = global::OpenAI.Chat.ChatTool;
using OAIChatToolCall = global::OpenAI.Chat.ChatToolCall;

namespace SharpClaw.OpenRouter;

/// <summary>
/// <see cref="IAgentBackend"/> implementation that uses the OpenRouter API.
/// OpenRouter exposes an OpenAI-compatible Chat Completions endpoint, so this backend
/// reuses the OpenAI SDK with a custom base URL of <c>https://openrouter.ai/api/v1</c>.
/// Manages the tool-use loop internally, delegating tool execution to the caller-supplied dispatcher.
/// </summary>
public sealed class OpenRouterBackend : IAgentBackend
{
    private const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1";

    private readonly ChatClient _chatClient;

    public OpenRouterBackend(string apiKey, string model = "openai/gpt-4o-mini")
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(OpenRouterBaseUrl),
        };
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), options);
        _chatClient = client.GetChatClient(model);
    }

    public async Task<string> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<ToolSchema> tools,
        IReadOnlyList<CoreChatMessage> history,
        Func<CoreToolCall, CancellationToken, Task<ToolCallResult>> toolDispatcher,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var openAiTools = tools.Select(ToOpenAiTool).ToList();
        var options = new ChatCompletionOptions();
        foreach (var tool in openAiTools)
            options.Tools.Add(tool);

        var messages = BuildMessages(systemPrompt, history);

        var iteration = 0;
        while (true)
        {
            onProgress?.Invoke(iteration == 0 ? "Thinking…" : "Processing tool results…");

            var response = await _chatClient.CompleteChatAsync(messages, options, cancellationToken);
            var completion = response.Value;

            if (completion.FinishReason != ChatFinishReason.ToolCalls)
            {
                return completion.Content.Count > 0 ? completion.Content[0].Text : string.Empty;
            }

            // Append assistant turn (with tool calls) to conversation.
            messages.Add(new AssistantChatMessage(completion));

            // Execute each tool call and append results.
            foreach (var toolCall in completion.ToolCalls)
            {
                onProgress?.Invoke($"  ↳ {toolCall.FunctionName}");

                var parsedArgs = ParseArgs(toolCall.FunctionArguments.ToString());
                var call = new CoreToolCall(toolCall.FunctionName, parsedArgs);
                var result = await toolDispatcher(call, cancellationToken);

                messages.Add(new ToolChatMessage(toolCall.Id, result.Content));
            }

            iteration++;
        }
    }

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        string systemPrompt,
        IReadOnlyList<ToolSchema> tools,
        IReadOnlyList<CoreChatMessage> history,
        Func<CoreToolCall, CancellationToken, Task<ToolCallResult>> toolDispatcher,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var openAiTools = tools.Select(ToOpenAiTool).ToList();
        var options = new ChatCompletionOptions();
        foreach (var tool in openAiTools)
            options.Tools.Add(tool);

        var messages = BuildMessages(systemPrompt, history);

        long totalInputTokens = 0;
        long totalOutputTokens = 0;

        while (true)
        {
            var stream = _chatClient.CompleteChatStreamingAsync(messages, options, cancellationToken);

            var fullText = new StringBuilder();
            // Accumulate tool call deltas keyed by their index.
            var toolCallBuilders = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();
            ChatFinishReason? finishReason = null;

            await foreach (var update in stream.WithCancellation(cancellationToken))
            {
                // Capture finish reason and usage.
                if (update.FinishReason.HasValue)
                    finishReason = update.FinishReason;

                if (update.Usage is { } usage)
                {
                    totalInputTokens += usage.InputTokenCount;
                    totalOutputTokens += usage.OutputTokenCount;
                }

                // Stream text tokens.
                foreach (var part in update.ContentUpdate)
                {
                    if (!string.IsNullOrEmpty(part.Text))
                    {
                        fullText.Append(part.Text);
                        yield return new TokenEvent(part.Text);
                    }
                }

                // Accumulate streaming tool call argument fragments.
                foreach (var toolCallUpdate in update.ToolCallUpdates)
                {
                    if (!toolCallBuilders.TryGetValue(toolCallUpdate.Index, out var entry))
                    {
                        entry = (toolCallUpdate.ToolCallId ?? string.Empty, toolCallUpdate.FunctionName ?? string.Empty, new StringBuilder());
                        toolCallBuilders[toolCallUpdate.Index] = entry;
                    }

                    var fragment = toolCallUpdate.FunctionArgumentsUpdate?.ToString();
                    if (!string.IsNullOrEmpty(fragment))
                        entry.Args.Append(fragment);
                }
            }

            if (finishReason != ChatFinishReason.ToolCalls)
            {
                yield return new UsageEvent("openrouter", totalInputTokens, totalOutputTokens);
                yield return new DoneEvent(fullText.ToString());
                yield break;
            }

            // Build the tool calls from accumulated deltas.
            var toolCalls = toolCallBuilders
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => OAIChatToolCall.CreateFunctionToolCall(
                    kvp.Value.Id,
                    kvp.Value.Name,
                    BinaryData.FromString(kvp.Value.Args.ToString())))
                .ToList();

            // Append assistant turn with tool calls.
            messages.Add(new AssistantChatMessage(toolCalls));

            // Execute tool calls and append results.
            var toolResults = new List<ToolChatMessage>();
            foreach (var toolCall in toolCalls)
            {
                var parsedArgs = ParseArgs(toolCall.FunctionArguments.ToString());
                yield return new ToolCallEvent(toolCall.FunctionName, parsedArgs);

                var call = new CoreToolCall(toolCall.FunctionName, parsedArgs);
                var result = await toolDispatcher(call, cancellationToken);
                yield return new ToolResultEvent(toolCall.FunctionName, result.Content, result.IsError);

                toolResults.Add(new ToolChatMessage(toolCall.Id, result.Content));
            }

            foreach (var toolResult in toolResults)
                messages.Add(toolResult);

            // Loop back to stream the model's next response.
        }
    }

    public ValueTask DisposeAsync() => default;

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static List<OAIChatMessage> BuildMessages(
        string systemPrompt,
        IReadOnlyList<CoreChatMessage> history)
    {
        var messages = new List<OAIChatMessage>
        {
            new SystemChatMessage(systemPrompt),
        };

        foreach (var msg in history)
        {
            messages.Add(msg.Role == ChatRole.User
                ? new UserChatMessage(msg.Content)
                : new AssistantChatMessage(msg.Content));
        }

        return messages;
    }

    private static OAIChatTool ToOpenAiTool(ToolSchema schema)
    {
        return OAIChatTool.CreateFunctionTool(
            functionName: schema.Name,
            functionDescription: schema.Description,
            functionParameters: BinaryData.FromString(schema.InputSchema.GetRawText()));
    }

    private static OpenRouterJsonArgs ParseArgs(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new OpenRouterJsonArgs(new Dictionary<string, JsonElement>());

        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
                ?? new Dictionary<string, JsonElement>();
            return new OpenRouterJsonArgs(dict);
        }
        catch
        {
            return new OpenRouterJsonArgs(new Dictionary<string, JsonElement>());
        }
    }
}

/// <summary>
/// Adapts <see cref="IReadOnlyDictionary{String, JsonElement}"/> to
/// <see cref="IReadOnlyDictionary{String, Object}"/> without copying entries.
/// </summary>
internal sealed class OpenRouterJsonArgs : IReadOnlyDictionary<string, object?>
{
    private readonly IReadOnlyDictionary<string, JsonElement> _inner;

    internal OpenRouterJsonArgs(IReadOnlyDictionary<string, JsonElement> inner) =>
        _inner = inner;

    public object? this[string key] => _inner[key];
    public IEnumerable<string> Keys => _inner.Keys;
    public IEnumerable<object?> Values => _inner.Values.Cast<object?>();
    public int Count => _inner.Count;
    public bool ContainsKey(string key) => _inner.ContainsKey(key);
    public bool TryGetValue(string key, out object? value)
    {
        if (_inner.TryGetValue(key, out var el)) { value = el; return true; }
        value = null;
        return false;
    }
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
        _inner.Select(kvp => KeyValuePair.Create(kvp.Key, (object?)kvp.Value)).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
