using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpClaw.Core;

namespace SharpClaw.Llm;

/// <summary>
/// LLM service that calls the Anthropic Messages API directly via HttpClient
/// with SSE streaming and an agentic tool-use loop.
/// </summary>
public sealed class GenericLlmService : ILlmService, IDisposable
{
    private readonly HttpClient _http;

    public string ServiceName => "llm";

    public GenericLlmService(string apiKey, string baseUrl = "https://api.anthropic.com")
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        string model,
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ToolSchema> tools,
        Func<ToolCall, CancellationToken, Task<ToolCallResult>> toolDispatcher,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = ConvertHistory(history);
        var fullContent = new StringBuilder();
        const int maxToolRounds = 20;

        for (var round = 0; round <= maxToolRounds; round++)
        {
            var body = BuildRequestBody(model, systemPrompt, messages, tools);
            var json = JsonSerializer.Serialize(body, JsonOpts);

            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            var textBuffer = new StringBuilder();
            var toolUses = new List<ToolUseAccumulator>();
            string? stopReason = null;
            int inputTokens = 0, outputTokens = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(ct);

                if (line is null) break;
                if (line.Length == 0) continue;                    // blank lines between SSE events
                if (!line.StartsWith("data: ")) continue;

                var data = line["data: ".Length..];
                if (data == "[DONE]") break;

                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                var eventType = root.GetProperty("type").GetString();

                switch (eventType)
                {
                    case "message_start":
                        if (root.TryGetProperty("message", out var msg) &&
                            msg.TryGetProperty("usage", out var startUsage))
                        {
                            inputTokens += startUsage.GetProperty("input_tokens").GetInt32();
                        }
                        break;

                    case "content_block_start":
                    {
                        var contentBlock = root.GetProperty("content_block");
                        var blockType = contentBlock.GetProperty("type").GetString();
                        if (blockType == "tool_use")
                        {
                            toolUses.Add(new ToolUseAccumulator
                            {
                                Index = root.GetProperty("index").GetInt32(),
                                Id = contentBlock.GetProperty("id").GetString()!,
                                Name = contentBlock.GetProperty("name").GetString()!,
                            });
                        }
                        break;
                    }

                    case "content_block_delta":
                    {
                        var delta = root.GetProperty("delta");
                        var deltaType = delta.GetProperty("type").GetString();

                        if (deltaType == "text_delta")
                        {
                            var text = delta.GetProperty("text").GetString() ?? "";
                            textBuffer.Append(text);
                            yield return new TokenEvent(text);
                        }
                        else if (deltaType == "input_json_delta")
                        {
                            var partialJson = delta.GetProperty("partial_json").GetString() ?? "";
                            var blockIdx = root.GetProperty("index").GetInt32();
                            var acc = toolUses.FirstOrDefault(t => t.Index == blockIdx);
                            acc?.InputJson.Append(partialJson);
                        }
                        break;
                    }

                    case "message_delta":
                        if (root.TryGetProperty("delta", out var msgDelta) &&
                            msgDelta.TryGetProperty("stop_reason", out var sr))
                        {
                            stopReason = sr.GetString();
                        }
                        if (root.TryGetProperty("usage", out var deltaUsage))
                        {
                            outputTokens += deltaUsage.GetProperty("output_tokens").GetInt32();
                        }
                        break;
                }
            }

            fullContent.Append(textBuffer);

            // If no tool use, we're done
            if (stopReason != "tool_use" || toolUses.Count == 0)
            {
                yield return new UsageEvent("anthropic", inputTokens, outputTokens);
                yield return new DoneEvent(fullContent.ToString());
                yield break;
            }

            // Build assistant message with text + tool_use content blocks
            var assistantContent = new List<object>();
            if (textBuffer.Length > 0)
                assistantContent.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = textBuffer.ToString() });
            foreach (var tu in toolUses)
            {
                var inputObj = JsonSerializer.Deserialize<JsonElement>(
                    tu.InputJson.Length > 0 ? tu.InputJson.ToString() : "{}");
                assistantContent.Add(new Dictionary<string, object>
                {
                    ["type"] = "tool_use", ["id"] = tu.Id, ["name"] = tu.Name, ["input"] = inputObj
                });
            }
            messages.Add(new ApiMessage("assistant", assistantContent));

            // Execute tools and add results
            var toolResultContent = new List<object>();
            foreach (var tu in toolUses)
            {
                yield return new ToolCallEvent(tu.Name, tu.InputJson.ToString());

                var toolCall = new ToolCall(tu.Name, tu.InputJson.ToString());
                var result = await toolDispatcher(toolCall, ct);

                yield return new ToolResultEvent(tu.Name, result.Content, result.IsError);
                toolResultContent.Add(new Dictionary<string, object>
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = tu.Id,
                    ["content"] = result.Content,
                    ["is_error"] = result.IsError,
                });
            }
            messages.Add(new ApiMessage("user", toolResultContent));

            yield return new UsageEvent("anthropic", inputTokens, outputTokens);
        }

        yield return new StatusEvent("Maximum tool rounds reached");
        yield return new DoneEvent(fullContent.ToString());
    }

    private static List<ApiMessage> ConvertHistory(IReadOnlyList<ChatMessage> history)
    {
        return history.Select(m => new ApiMessage(
            m.Role == ChatRole.Assistant ? "assistant" : "user",
            m.Content
        )).ToList();
    }

    private static Dictionary<string, object> BuildRequestBody(
        string model,
        string systemPrompt,
        List<ApiMessage> messages,
        IReadOnlyList<ToolSchema> tools)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = 16384,
            ["stream"] = true,
            ["system"] = systemPrompt,
            ["messages"] = messages,
        };

        if (tools.Count > 0)
        {
            body["tools"] = tools.Select(t => new Dictionary<string, object>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = JsonSerializer.Deserialize<JsonElement>(t.InputSchemaJson),
            }).ToList();
        }

        return body;
    }

    public void Dispose() => _http.Dispose();

    private sealed class ToolUseAccumulator
    {
        public int Index { get; init; }
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public StringBuilder InputJson { get; } = new();
    }

    private sealed record ApiMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] object Content);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
