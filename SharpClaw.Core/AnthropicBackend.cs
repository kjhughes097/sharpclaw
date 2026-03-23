using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using McpContentBlock = ModelContextProtocol.Protocol.ContentBlock;
using McpTextContentBlock = ModelContextProtocol.Protocol.TextContentBlock;

namespace SharpClaw.Core;

/// <summary>
/// <see cref="IAgentBackend"/> implementation that uses the Anthropic Messages API.
/// Manages the tool-use loop internally, delegating tool execution to the caller-supplied dispatcher.
/// </summary>
public sealed class AnthropicBackend : IAgentBackend
{
    private readonly AnthropicClient _anthropic;
    private readonly string _model;

    // Cached JsonElement representing the JSON string "object" — used as the
    // default InputSchema type when the MCP tool schema omits the "type" field.
    private static readonly JsonElement _objectTypeElement =
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

        // Seed conversation from the caller-supplied history.
        var messages = new List<Anthropic.Models.Messages.MessageParam>();
        foreach (var msg in history)
        {
            messages.Add(new Anthropic.Models.Messages.MessageParam
            {
                Role = msg.Role == ChatRole.User
                    ? Anthropic.Models.Messages.Role.User
                    : Anthropic.Models.Messages.Role.Assistant,
                Content = new Anthropic.Models.Messages.MessageParamContent(msg.Content),
            });
        }

        var iteration = 0;
        while (true)
        {
            onProgress?.Invoke(iteration == 0 ? "Thinking…" : "Processing tool results…");

            var response = await _anthropic.Messages.Create(
                new Anthropic.Models.Messages.MessageCreateParams
                {
                    Model = _model,
                    MaxTokens = 4096,
                    System = systemPrompt,
                    Tools = anthropicTools,
                    Messages = messages,
                },
                cancellationToken);

            // Append the assistant turn to the conversation history.
            var assistantBlocks = ResponseBlocksToParams(response.Content);
            messages.Add(new Anthropic.Models.Messages.MessageParam
            {
                Role = Anthropic.Models.Messages.Role.Assistant,
                Content = new Anthropic.Models.Messages.MessageParamContent(assistantBlocks),
            });

            if (response.StopReason?.Value() != Anthropic.Models.Messages.StopReason.ToolUse)
            {
                foreach (var block in response.Content)
                {
                    if (block.TryPickText(out var textBlock))
                        return textBlock.Text;
                }
                return string.Empty;
            }

            // Execute every tool call the model requested.
            var toolResults = new List<Anthropic.Models.Messages.ContentBlockParam>();

            foreach (var block in response.Content)
            {
                if (!block.TryPickToolUse(out var toolUse))
                    continue;

                onProgress?.Invoke($"  ↳ {toolUse.Name}");

                var args = new JsonElementArgs(toolUse.Input);
                var call = new ToolCall(toolUse.Name, args);
                var result = await toolDispatcher(call, cancellationToken);

                toolResults.Add(new Anthropic.Models.Messages.ContentBlockParam(
                    new Anthropic.Models.Messages.ToolResultBlockParam(toolUse.ID)
                    {
                        Content = new Anthropic.Models.Messages.ToolResultBlockParamContent(result.Content),
                        IsError = result.IsError,
                    },
                    element: null));
            }

            messages.Add(new Anthropic.Models.Messages.MessageParam
            {
                Role = Anthropic.Models.Messages.Role.User,
                Content = new Anthropic.Models.Messages.MessageParamContent(toolResults),
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

        var messages = new List<Anthropic.Models.Messages.MessageParam>();
        foreach (var msg in history)
        {
            messages.Add(new Anthropic.Models.Messages.MessageParam
            {
                Role = msg.Role == ChatRole.User
                    ? Anthropic.Models.Messages.Role.User
                    : Anthropic.Models.Messages.Role.Assistant,
                Content = new Anthropic.Models.Messages.MessageParamContent(msg.Content),
            });
        }

        while (true)
        {
            var stream = _anthropic.Messages.CreateStreaming(
                new Anthropic.Models.Messages.MessageCreateParams
                {
                    Model = _model,
                    MaxTokens = 4096,
                    System = systemPrompt,
                    Tools = anthropicTools,
                    Messages = messages,
                },
                cancellationToken);

            // Accumulate the full response for the conversation history / tool-use loop.
            var fullText = new StringBuilder();
            var assistantBlocks = new List<Anthropic.Models.Messages.ContentBlockParam>();
            var pendingTools = new List<(string Id, string Name, string InputJson)>();
            string? curToolId = null;
            string? curToolName = null;
            var curToolInput = new StringBuilder();
            var hasToolUse = false;

            await foreach (var evt in stream.WithCancellation(cancellationToken))
            {
                if (evt.TryPickContentBlockStart(out var blockStart))
                {
                    if (blockStart.ContentBlock.TryPickToolUse(out var toolUse))
                    {
                        curToolId = toolUse.ID;
                        curToolName = toolUse.Name;
                        curToolInput.Clear();
                    }
                }
                else if (evt.TryPickContentBlockDelta(out var blockDelta))
                {
                    if (blockDelta.Delta.TryPickText(out var textDelta))
                    {
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

                        // Build the assistant-side ToolUseBlockParam for the conversation history.
                        var parsedInput = string.IsNullOrEmpty(inputJson)
                            ? new Dictionary<string, JsonElement>()
                            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(inputJson)
                              ?? new Dictionary<string, JsonElement>();

                        assistantBlocks.Add(new Anthropic.Models.Messages.ContentBlockParam(
                            new Anthropic.Models.Messages.ToolUseBlockParam
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
                    else if (fullText.Length > 0)
                    {
                        // Text block finished — add to assistant blocks for history.
                        assistantBlocks.Add(new Anthropic.Models.Messages.ContentBlockParam(
                            new Anthropic.Models.Messages.TextBlockParam(fullText.ToString()),
                            element: null));
                    }
                }
            }

            // Append the assistant turn to the conversation.
            if (assistantBlocks.Count > 0)
            {
                messages.Add(new Anthropic.Models.Messages.MessageParam
                {
                    Role = Anthropic.Models.Messages.Role.Assistant,
                    Content = new Anthropic.Models.Messages.MessageParamContent(assistantBlocks),
                });
            }

            if (!hasToolUse)
            {
                yield return new DoneEvent(fullText.ToString());
                yield break;
            }

            // Execute all pending tool calls and build the user turn.
            var toolResults = new List<Anthropic.Models.Messages.ContentBlockParam>();
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

                toolResults.Add(new Anthropic.Models.Messages.ContentBlockParam(
                    new Anthropic.Models.Messages.ToolResultBlockParam(id)
                    {
                        Content = new Anthropic.Models.Messages.ToolResultBlockParamContent(result.Content),
                        IsError = result.IsError,
                    },
                    element: null));
            }

            messages.Add(new Anthropic.Models.Messages.MessageParam
            {
                Role = Anthropic.Models.Messages.Role.User,
                Content = new Anthropic.Models.Messages.MessageParamContent(toolResults),
            });

            // Loop back to stream the model's next response.
        }
    }

    public ValueTask DisposeAsync() => default;

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static List<Anthropic.Models.Messages.ContentBlockParam> ResponseBlocksToParams(
        IReadOnlyList<Anthropic.Models.Messages.ContentBlock> blocks)
    {
        var result = new List<Anthropic.Models.Messages.ContentBlockParam>(blocks.Count);

        foreach (var block in blocks)
        {
            if (block.TryPickText(out var textBlock))
            {
                result.Add(new Anthropic.Models.Messages.ContentBlockParam(
                    new Anthropic.Models.Messages.TextBlockParam(textBlock.Text),
                    element: null));
            }
            else if (block.TryPickToolUse(out var toolUse))
            {
                result.Add(new Anthropic.Models.Messages.ContentBlockParam(
                    new Anthropic.Models.Messages.ToolUseBlockParam
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

    private static Anthropic.Models.Messages.ToolUnion ToAnthropicTool(ToolSchema schema)
    {
        var typeEl = schema.InputSchema.TryGetProperty("type", out var t)
            ? t
            : _objectTypeElement;

        Dictionary<string, JsonElement>? propsDict = null;
        if (schema.InputSchema.TryGetProperty("properties", out var propsEl))
            propsDict = propsEl.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

        List<string>? requiredList = null;
        if (schema.InputSchema.TryGetProperty("required", out var reqEl))
            requiredList = reqEl.EnumerateArray().Select(e => e.GetString()!).ToList();

        var inputSchema = new Anthropic.Models.Messages.InputSchema
        {
            Type = typeEl,
            Properties = propsDict,
            Required = requiredList,
        };

        return new Anthropic.Models.Messages.ToolUnion(new Anthropic.Models.Messages.Tool
        {
            Name = schema.Name,
            Description = schema.Description,
            InputSchema = inputSchema,
        });
    }
}

/// <summary>
/// Adapts <see cref="IReadOnlyDictionary{String, JsonElement}"/> to
/// <see cref="IReadOnlyDictionary{String, Object}"/> without copying entries.
/// </summary>
internal sealed class JsonElementArgs : IReadOnlyDictionary<string, object?>
{
    private readonly IReadOnlyDictionary<string, JsonElement> _inner;

    internal JsonElementArgs(IReadOnlyDictionary<string, JsonElement> inner) =>
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
