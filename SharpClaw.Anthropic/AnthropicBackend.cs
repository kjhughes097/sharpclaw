using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SharpClaw.Core;
using global::Anthropic;
using AnthropicMessages = global::Anthropic.Models.Messages;
using McpContentBlock = ModelContextProtocol.Protocol.ContentBlock;
using McpTextContentBlock = ModelContextProtocol.Protocol.TextContentBlock;

namespace SharpClaw.Anthropic;

/// <summary>
/// <see cref="IAgentBackend"/> implementation that uses the Anthropic Messages API.
/// Manages the tool-use loop internally, delegating tool execution to the caller-supplied dispatcher.
/// </summary>
public sealed class AnthropicBackend : IAgentBackend
{
    private readonly AnthropicClient _anthropic;
    private readonly string _model;

    private static readonly JsonElement ObjectTypeElement =
        JsonDocument.Parse("\"object\"").RootElement.Clone();

    public AnthropicBackend(AnthropicClient anthropic, string model = "claude-haiku-4-5-20251001")
    {
        _anthropic = anthropic;
        _model = model;
    }

    public async Task<string> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<ToolSchema> tools,
        IReadOnlyList<ChatMessage> history,
        Func<ToolCall, CancellationToken, Task<ToolCallResult>> toolDispatcher,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var anthropicTools = tools.Select(ToAnthropicTool).ToList();

        var messages = new List<AnthropicMessages.MessageParam>();
        foreach (var msg in history)
        {
            messages.Add(new AnthropicMessages.MessageParam
            {
                Role = msg.Role == ChatRole.User
                    ? AnthropicMessages.Role.User
                    : AnthropicMessages.Role.Assistant,
                Content = new AnthropicMessages.MessageParamContent(msg.Content),
            });
        }

        var iteration = 0;
        while (true)
        {
            onProgress?.Invoke(iteration == 0 ? "Thinking..." : "Processing tool results...");

            var response = await _anthropic.Messages.Create(
                new AnthropicMessages.MessageCreateParams
                {
                    Model = _model,
                    MaxTokens = 4096,
                    System = systemPrompt,
                    Tools = anthropicTools,
                    Messages = messages,
                },
                cancellationToken);

            var assistantBlocks = ResponseBlocksToParams(response.Content);
            messages.Add(new AnthropicMessages.MessageParam
            {
                Role = AnthropicMessages.Role.Assistant,
                Content = new AnthropicMessages.MessageParamContent(assistantBlocks),
            });

            if (response.StopReason?.Value() != AnthropicMessages.StopReason.ToolUse)
            {
                foreach (var block in response.Content)
                {
                    if (block.TryPickText(out var textBlock))
                        return textBlock.Text;
                }

                return string.Empty;
            }

            var toolResults = new List<AnthropicMessages.ContentBlockParam>();

            foreach (var block in response.Content)
            {
                if (!block.TryPickToolUse(out var toolUse))
                    continue;

                onProgress?.Invoke($"  -> {toolUse.Name}");

                var args = new JsonElementArgs(toolUse.Input);
                var call = new ToolCall(toolUse.Name, args);
                var result = await toolDispatcher(call, cancellationToken);

                toolResults.Add(new AnthropicMessages.ContentBlockParam(
                    new AnthropicMessages.ToolResultBlockParam(toolUse.ID)
                    {
                        Content = new AnthropicMessages.ToolResultBlockParamContent(result.Content),
                        IsError = result.IsError,
                    },
                    element: null));
            }

            messages.Add(new AnthropicMessages.MessageParam
            {
                Role = AnthropicMessages.Role.User,
                Content = new AnthropicMessages.MessageParamContent(toolResults),
            });

            iteration++;
        }
    }

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        string systemPrompt,
        IReadOnlyList<ToolSchema> tools,
        IReadOnlyList<ChatMessage> history,
        Func<ToolCall, CancellationToken, Task<ToolCallResult>> toolDispatcher,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var anthropicTools = tools.Select(ToAnthropicTool).ToList();

        var messages = new List<AnthropicMessages.MessageParam>();
        foreach (var msg in history)
        {
            messages.Add(new AnthropicMessages.MessageParam
            {
                Role = msg.Role == ChatRole.User
                    ? AnthropicMessages.Role.User
                    : AnthropicMessages.Role.Assistant,
                Content = new AnthropicMessages.MessageParamContent(msg.Content),
            });
        }

        long totalInputTokens = 0;
        long totalOutputTokens = 0;

        while (true)
        {
            var stream = _anthropic.Messages.CreateStreaming(
                new AnthropicMessages.MessageCreateParams
                {
                    Model = _model,
                    MaxTokens = 4096,
                    System = systemPrompt,
                    Tools = anthropicTools,
                    Messages = messages,
                },
                cancellationToken);

            var fullText = new StringBuilder();
            var assistantBlocks = new List<AnthropicMessages.ContentBlockParam>();
            var pendingTools = new List<(string Id, string Name, string InputJson)>();
            StringBuilder? curTextBlock = null;
            string? curToolId = null;
            string? curToolName = null;
            var curToolInput = new StringBuilder();
            var hasToolUse = false;
            long iterationInputTokens = 0;
            long iterationOutputTokens = 0;

            await foreach (var evt in stream.WithCancellation(cancellationToken))
            {
                if (evt.TryPickDelta(out var messageDelta))
                {
                    if (messageDelta.Usage is { } usage)
                    {
                        iterationInputTokens = (usage.InputTokens ?? 0)
                            + (usage.CacheCreationInputTokens ?? 0)
                            + (usage.CacheReadInputTokens ?? 0);
                        iterationOutputTokens = usage.OutputTokens;
                    }
                }
                else if (evt.TryPickContentBlockStart(out var blockStart))
                {
                    if (blockStart.ContentBlock.TryPickToolUse(out var toolUse))
                    {
                        curTextBlock = null;
                        curToolId = toolUse.ID;
                        curToolName = toolUse.Name;
                        curToolInput.Clear();
                    }
                    else if (blockStart.ContentBlock.TryPickText(out var textBlock))
                    {
                        curTextBlock = new StringBuilder();
                        if (!string.IsNullOrEmpty(textBlock.Text))
                        {
                            curTextBlock.Append(textBlock.Text);
                            fullText.Append(textBlock.Text);
                            yield return new TokenEvent(textBlock.Text);
                        }
                    }
                }
                else if (evt.TryPickContentBlockDelta(out var blockDelta))
                {
                    if (blockDelta.Delta.TryPickText(out var textDelta))
                    {
                        curTextBlock ??= new StringBuilder();
                        curTextBlock.Append(textDelta.Text);
                        fullText.Append(textDelta.Text);
                        yield return new TokenEvent(textDelta.Text);
                    }
                    else if (blockDelta.Delta.TryPickInputJson(out var jsonDelta))
                    {
                        curToolInput.Append(jsonDelta.PartialJson);
                    }
                }
                else if (evt.TryPickContentBlockStop(out _))
                {
                    if (curToolId is not null)
                    {
                        var inputJson = curToolInput.ToString();
                        pendingTools.Add((curToolId, curToolName!, inputJson));

                        var parsedInput = string.IsNullOrEmpty(inputJson)
                            ? new Dictionary<string, JsonElement>()
                            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(inputJson)
                              ?? new Dictionary<string, JsonElement>();

                        assistantBlocks.Add(new AnthropicMessages.ContentBlockParam(
                            new AnthropicMessages.ToolUseBlockParam
                            {
                                ID = curToolId,
                                Name = curToolName!,
                                Input = parsedInput,
                            },
                            element: null));

                        hasToolUse = true;
                        curToolId = null;
                        curToolName = null;
                    }
                    else if (curTextBlock is not null)
                    {
                        if (curTextBlock.Length > 0)
                        {
                            assistantBlocks.Add(new AnthropicMessages.ContentBlockParam(
                                new AnthropicMessages.TextBlockParam(curTextBlock.ToString()),
                                element: null));
                        }

                        curTextBlock = null;
                    }
                }
            }

            totalInputTokens += iterationInputTokens;
            totalOutputTokens += iterationOutputTokens;

            if (assistantBlocks.Count > 0)
            {
                messages.Add(new AnthropicMessages.MessageParam
                {
                    Role = AnthropicMessages.Role.Assistant,
                    Content = new AnthropicMessages.MessageParamContent(assistantBlocks),
                });
            }

            if (!hasToolUse)
            {
                yield return new UsageEvent("anthropic", totalInputTokens, totalOutputTokens);
                yield return new DoneEvent(fullText.ToString());
                yield break;
            }

            var toolResults = new List<AnthropicMessages.ContentBlockParam>();
            foreach (var (id, name, inputJson) in pendingTools)
            {
                var parsedArgs = string.IsNullOrEmpty(inputJson)
                    ? new Dictionary<string, JsonElement>()
                    : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(inputJson)
                      ?? new Dictionary<string, JsonElement>();

                var adaptedArgs = new JsonElementArgs(parsedArgs);
                yield return new ToolCallEvent(name, adaptedArgs);

                var call = new ToolCall(name, adaptedArgs);
                var result = await toolDispatcher(call, cancellationToken);
                yield return new ToolResultEvent(name, result.Content, result.IsError);

                toolResults.Add(new AnthropicMessages.ContentBlockParam(
                    new AnthropicMessages.ToolResultBlockParam(id)
                    {
                        Content = new AnthropicMessages.ToolResultBlockParamContent(result.Content),
                        IsError = result.IsError,
                    },
                    element: null));
            }

            messages.Add(new AnthropicMessages.MessageParam
            {
                Role = AnthropicMessages.Role.User,
                Content = new AnthropicMessages.MessageParamContent(toolResults),
            });
        }
    }

    public ValueTask DisposeAsync() => default;

    private static List<AnthropicMessages.ContentBlockParam> ResponseBlocksToParams(
        IReadOnlyList<AnthropicMessages.ContentBlock> blocks)
    {
        var result = new List<AnthropicMessages.ContentBlockParam>(blocks.Count);

        foreach (var block in blocks)
        {
            if (block.TryPickText(out var textBlock))
            {
                result.Add(new AnthropicMessages.ContentBlockParam(
                    new AnthropicMessages.TextBlockParam(textBlock.Text),
                    element: null));
            }
            else if (block.TryPickToolUse(out var toolUse))
            {
                result.Add(new AnthropicMessages.ContentBlockParam(
                    new AnthropicMessages.ToolUseBlockParam
                    {
                        ID = toolUse.ID,
                        Name = toolUse.Name,
                        Input = toolUse.Input,
                    },
                    element: null));
            }
        }

        return result;
    }

    private static string ExtractText(IList<McpContentBlock> content)
    {
        var parts = content
            .OfType<McpTextContentBlock>()
            .Select(t => t.Text);
        return string.Join("\n", parts);
    }

    private static AnthropicMessages.ToolUnion ToAnthropicTool(ToolSchema schema)
    {
        var typeEl = schema.InputSchema.TryGetProperty("type", out var t)
            ? t
            : ObjectTypeElement;

        Dictionary<string, JsonElement>? propsDict = null;
        if (schema.InputSchema.TryGetProperty("properties", out var propsEl))
            propsDict = propsEl.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

        List<string>? requiredList = null;
        if (schema.InputSchema.TryGetProperty("required", out var reqEl))
            requiredList = reqEl.EnumerateArray().Select(e => e.GetString()!).ToList();

        var inputSchema = new AnthropicMessages.InputSchema
        {
            Type = typeEl,
            Properties = propsDict,
            Required = requiredList,
        };

        return new AnthropicMessages.ToolUnion(new AnthropicMessages.Tool
        {
            Name = schema.Name,
            Description = schema.Description,
            InputSchema = inputSchema,
        });
    }
}