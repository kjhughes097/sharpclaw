using System.Text.Json;
using Anthropic;
using ModelContextProtocol.Client;
using McpContentBlock = ModelContextProtocol.Protocol.ContentBlock;
using McpTextContentBlock = ModelContextProtocol.Protocol.TextContentBlock;

namespace SharpClaw.Core;

/// <summary>
/// Runs an Anthropic Messages API tool-use loop, dispatching tool calls through
/// a connected MCP client until Claude returns a final text response.
/// </summary>
public sealed class MessageLoop
{
    private readonly AnthropicClient _anthropic;
    private readonly string _model;

    // Cached JsonElement representing the JSON string "object" — used as the
    // default InputSchema type when the MCP tool schema omits the "type" field.
    private static readonly JsonElement _objectTypeElement =
        JsonDocument.Parse("\"object\"").RootElement.Clone();

    public MessageLoop(AnthropicClient anthropic, string model = "claude-3-5-haiku-20241022")
    {
        _anthropic = anthropic;
        _model = model;
    }

    /// <summary>
    /// Runs the conversation loop until Claude produces a final answer.
    /// </summary>
    /// <param name="systemPrompt">System prompt to send on every turn.</param>
    /// <param name="tools">Anthropic tool schemas to expose to Claude.</param>
    /// <param name="userMessage">Initial user message.</param>
    /// <param name="mcpClient">MCP client used to execute tool calls.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Claude's final text response.</returns>
    public async Task<string> RunAsync(
        string systemPrompt,
        IReadOnlyList<Anthropic.Models.Messages.ToolUnion> tools,
        string userMessage,
        McpClient mcpClient,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<Anthropic.Models.Messages.MessageParam>
        {
            new()
            {
                Role = Anthropic.Models.Messages.Role.User,
                Content = new Anthropic.Models.Messages.MessageParamContent(userMessage),
            }
        };

        while (true)
        {
            var response = await _anthropic.Messages.Create(
                new Anthropic.Models.Messages.MessageCreateParams
                {
                    Model = _model,
                    MaxTokens = 4096,
                    System = systemPrompt,
                    Tools = tools,
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
                // Claude is done — extract the first text block.
                foreach (var block in response.Content)
                {
                    if (block.TryPickText(out var textBlock))
                        return textBlock.Text;
                }
                return string.Empty;
            }

            // Execute every tool call Claude requested.
            var toolResults = new List<Anthropic.Models.Messages.ContentBlockParam>();

            foreach (var block in response.Content)
            {
                if (!block.TryPickToolUse(out var toolUse))
                    continue;

                // Adapt IReadOnlyDictionary<string, JsonElement> → IReadOnlyDictionary<string, object?>
                // without copying: forward the original dict through a lightweight adapter.
                var callResult = await mcpClient.CallToolAsync(
                    toolUse.Name,
                    new JsonElementArgs(toolUse.Input),
                    cancellationToken: cancellationToken);

                var resultText = ExtractText(callResult.Content);

                toolResults.Add(new Anthropic.Models.Messages.ContentBlockParam(
                    new Anthropic.Models.Messages.ToolResultBlockParam(toolUse.ID)
                    {
                        Content = new Anthropic.Models.Messages.ToolResultBlockParamContent(resultText),
                        IsError = callResult.IsError,
                    },
                    element: null));
            }

            // Feed tool results back as the next user turn.
            messages.Add(new Anthropic.Models.Messages.MessageParam
            {
                Role = Anthropic.Models.Messages.Role.User,
                Content = new Anthropic.Models.Messages.MessageParamContent(toolResults),
            });
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts API response <see cref="Anthropic.Models.Messages.ContentBlock"/> items
    /// into the <see cref="Anthropic.Models.Messages.ContentBlockParam"/> form needed for
    /// conversation history.
    /// </summary>
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

    /// <summary>
    /// Joins all text content blocks from an MCP tool result into a single string.
    /// </summary>
    private static string ExtractText(IList<McpContentBlock> content)
    {
        var parts = content
            .OfType<McpTextContentBlock>()
            .Select(t => t.Text);
        return string.Join("\n", parts);
    }

    // ─── Static factory ─────────────────────────────────────────────────────

    /// <summary>
    /// Converts an <see cref="McpClientTool"/> into an Anthropic <see cref="Anthropic.Models.Messages.ToolUnion"/>.
    /// </summary>
    public static Anthropic.Models.Messages.ToolUnion ToAnthropicTool(McpClientTool mcpTool)
    {
        var schema = mcpTool.JsonSchema;

        var typeEl = schema.TryGetProperty("type", out var t)
            ? t
            : _objectTypeElement;

        Dictionary<string, JsonElement>? propsDict = null;
        if (schema.TryGetProperty("properties", out var propsEl))
            propsDict = propsEl.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

        List<string>? requiredList = null;
        if (schema.TryGetProperty("required", out var reqEl))
            requiredList = reqEl.EnumerateArray().Select(e => e.GetString()!).ToList();

        var inputSchema = new Anthropic.Models.Messages.InputSchema
        {
            Type = typeEl,
            Properties = propsDict,
            Required = requiredList,
        };

        return new Anthropic.Models.Messages.ToolUnion(new Anthropic.Models.Messages.Tool
        {
            Name = mcpTool.Name,
            Description = mcpTool.Description,
            InputSchema = inputSchema,
        });
    }
}

/// <summary>
/// Adapts <see cref="IReadOnlyDictionary{String, JsonElement}"/> to
/// <see cref="IReadOnlyDictionary{String, Object}"/> without copying entries,
/// so that tool-call arguments can be forwarded to the MCP client as-is.
/// </summary>
file sealed class JsonElementArgs : IReadOnlyDictionary<string, object?>
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
