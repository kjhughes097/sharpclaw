using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Gateway.Providers;

/// <summary>Streams chat completions from OpenRouter, translating its SSE stream to events.</summary>
public sealed class OpenRouterProvider : IChatProvider
{
    private readonly HttpClient _http;
    private readonly OpenRouterOptions _openRouter;
    private readonly ProviderOptions _provider;

    public OpenRouterProvider(
        HttpClient http, IOptions<OpenRouterOptions> openRouter, IOptions<ProviderOptions> provider)
    {
        _http = http;
        _openRouter = openRouter.Value;
        _provider = provider.Value;
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ProviderChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = BuildBody(request);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_openRouter.BaseUrl}/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_openRouter.ApiKey}");
        httpRequest.Headers.TryAddWithoutValidation("X-Title", "SharpClaw");

        HttpResponseMessage response;
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(_provider.ConnectTimeoutSeconds));
            try
            {
                response = await _http.SendAsync(
                    httpRequest, HttpCompletionOption.ResponseHeadersRead, connectCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ProviderException(ProviderErrorKind.Timeout, null, "connect timeout");
            }
            catch (HttpRequestException ex)
            {
                throw new ProviderException(ProviderErrorKind.Upstream, null, ex.Message);
            }
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                if (await IsContextLengthErrorAsync(response, ct))
                    throw new ProviderException(ProviderErrorKind.ContextLength, (int)response.StatusCode, "context length exceeded");
                throw new ProviderException(ProviderErrorKind.Upstream, (int)response.StatusCode, "upstream non-2xx");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? finishReason = null;
            ChatStreamEvent.Usage? usage = null;
            // Tool calls arrive as fragments keyed by index; emit each once it is complete.
            var toolCalls = new SortedDictionary<int, ToolCallBuilder>();

            while (true)
            {
                string? line;
                using (var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    idleCts.CancelAfter(TimeSpan.FromSeconds(_provider.IdleTimeoutSeconds));
                    try
                    {
                        line = await reader.ReadLineAsync(idleCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new ProviderException(ProviderErrorKind.Timeout, null, "idle timeout");
                    }
                }

                if (line is null) break;
                if (line.Length == 0) continue;
                if (line.StartsWith(':')) continue; // comment, e.g. ": OPENROUTER PROCESSING"
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var data = line[5..].Trim();
                if (data == "[DONE]") break;

                var parsed = ParseChunk(data);
                foreach (var text in parsed.Deltas)
                    yield return new ChatStreamEvent.Delta(text);
                foreach (var fragment in parsed.ToolCallFragments)
                {
                    if (!toolCalls.TryGetValue(fragment.Index, out var builder))
                        toolCalls[fragment.Index] = builder = new ToolCallBuilder();
                    builder.Add(fragment);
                }
                if (parsed.FinishReason is not null)
                    finishReason = parsed.FinishReason;
                if (parsed.Usage is not null)
                    usage = parsed.Usage;
            }

            foreach (var call in toolCalls.Values)
                yield return call.Build();
            if (usage is not null)
                yield return usage;
            yield return new ChatStreamEvent.Done(finishReason ?? (toolCalls.Count > 0 ? "tool_calls" : "stop"));
        }
    }

    private readonly record struct ChunkResult(
        IReadOnlyList<string> Deltas, IReadOnlyList<ToolCallFragment> ToolCallFragments,
        string? FinishReason, ChatStreamEvent.Usage? Usage);

    /// <summary>Part of a streamed tool call; any field may be absent in a given chunk.</summary>
    private readonly record struct ToolCallFragment(int Index, string? Id, string? Name, string? Arguments);

    private sealed class ToolCallBuilder
    {
        private string? _id;
        private string? _name;
        private readonly StringBuilder _arguments = new();

        public void Add(ToolCallFragment f)
        {
            _id ??= f.Id;
            _name ??= f.Name;
            _arguments.Append(f.Arguments);
        }

        public ChatStreamEvent.ToolCall Build()
        {
            if (string.IsNullOrEmpty(_name))
                throw new ProviderException(ProviderErrorKind.Upstream, null, "upstream tool call without a name");
            var args = _arguments.Length == 0 ? "{}" : _arguments.ToString();
            return new ChatStreamEvent.ToolCall(_id ?? $"call_{Guid.NewGuid():N}", _name, args);
        }
    }

    /// <summary>
    /// Reads at most 4 KB of an error body, only to recognise a context-length error. The body is
    /// never logged or stored (it may echo the prompt).
    /// </summary>
    private static async Task<bool> IsContextLengthErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if ((int)response.StatusCode is not (400 or 413))
            return false;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[4096];
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            return text.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase)
                || text.Contains("context length", StringComparison.OrdinalIgnoreCase)
                || text.Contains("context window", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return false;
        }
    }

    private static ChunkResult ParseChunk(string data)
    {
        var deltas = new List<string>();
        var fragments = new List<ToolCallFragment>();
        string? finishReason = null;
        ChatStreamEvent.Usage? usage = null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(data); }
        catch (JsonException) { return new ChunkResult(deltas, fragments, null, null); }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out _))
                throw new ProviderException(ProviderErrorKind.Upstream, null, "upstream error chunk");

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("content", out var content) &&
                        content.ValueKind == JsonValueKind.String)
                    {
                        var text = content.GetString();
                        if (!string.IsNullOrEmpty(text))
                            deltas.Add(text);
                    }

                    if (choice.TryGetProperty("delta", out var toolDelta) &&
                        toolDelta.TryGetProperty("tool_calls", out var calls) &&
                        calls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var call in calls.EnumerateArray())
                            fragments.Add(ParseFragment(call));
                    }

                    if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                        finishReason = fr.GetString();
                }
            }

            if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
            {
                var prompt = usageEl.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pv) ? pv : 0;
                var completion = usageEl.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var cv) ? cv : 0;
                double? cost = usageEl.TryGetProperty("cost", out var co) && co.ValueKind == JsonValueKind.Number
                    ? co.GetDouble() : null;
                usage = new ChatStreamEvent.Usage(prompt, completion, cost);
            }
        }

        return new ChunkResult(deltas, fragments, finishReason, usage);
    }

    private static ToolCallFragment ParseFragment(JsonElement call)
    {
        var index = call.TryGetProperty("index", out var i) && i.TryGetInt32(out var iv) ? iv : 0;
        var id = call.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
        string? name = null, args = null;
        if (call.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
        {
            if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString();
            if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String) args = a.GetString();
        }
        return new ToolCallFragment(index, id, name, args);
    }

    private static void WriteMessage(Utf8JsonWriter w, ProviderChatMessage m)
    {
        w.WriteStartObject();
        w.WriteString("role", m.Role);
        if (m.Images is { Count: > 0 })
            WriteContentParts(w, m.Content, m.Images);
        else if (m.Content is null) w.WriteNull("content");
        else w.WriteString("content", m.Content);
        if (m.ToolCallId is { } toolCallId) w.WriteString("tool_call_id", toolCallId);
        if (m.ToolCalls is { Count: > 0 })
        {
            w.WritePropertyName("tool_calls");
            w.WriteStartArray();
            foreach (var c in m.ToolCalls)
            {
                w.WriteStartObject();
                w.WriteString("id", c.Id);
                w.WriteString("type", "function");
                w.WritePropertyName("function");
                w.WriteStartObject();
                w.WriteString("name", c.Name);
                w.WriteString("arguments", c.ArgumentsJson);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    /// <summary>OpenAI content parts: the text (when there is any), then one <c>image_url</c> per image.</summary>
    private static void WriteContentParts(Utf8JsonWriter w, string? text, IReadOnlyList<ProviderImage> images)
    {
        w.WritePropertyName("content");
        w.WriteStartArray();
        if (!string.IsNullOrEmpty(text))
        {
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", text);
            w.WriteEndObject();
        }
        foreach (var image in images)
        {
            w.WriteStartObject();
            w.WriteString("type", "image_url");
            w.WritePropertyName("image_url");
            w.WriteStartObject();
            w.WriteString("url", image.DataUri());
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteTool(Utf8JsonWriter w, ProviderTool t)
    {
        w.WriteStartObject();
        w.WriteString("type", "function");
        w.WritePropertyName("function");
        w.WriteStartObject();
        w.WriteString("name", t.Name);
        if (t.Description is { } d) w.WriteString("description", d);
        if (t.ParametersJson is { } p)
        {
            w.WritePropertyName("parameters");
            w.WriteRawValue(p);
        }
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private string BuildBody(ProviderChatRequest request)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", request.Model);
            w.WriteBoolean("stream", true);

            w.WritePropertyName("messages");
            w.WriteStartArray();
            foreach (var m in request.Messages)
                WriteMessage(w, m);
            w.WriteEndArray();

            if (request.Tools is { Count: > 0 })
            {
                w.WritePropertyName("tools");
                w.WriteStartArray();
                foreach (var tool in request.Tools)
                    WriteTool(w, tool);
                w.WriteEndArray();
            }
            if (request.ToolChoice is { } choice) w.WriteString("tool_choice", choice);

            if (request.Temperature is { } t) w.WriteNumber("temperature", t);
            if (request.MaxTokens is { } mt) w.WriteNumber("max_tokens", mt);
            if (request.TopP is { } tp) w.WriteNumber("top_p", tp);
            if (request.Stop is { Count: > 0 })
            {
                w.WritePropertyName("stop");
                w.WriteStartArray();
                foreach (var s in request.Stop) w.WriteStringValue(s);
                w.WriteEndArray();
            }

            w.WritePropertyName("stream_options");
            w.WriteStartObject();
            w.WriteBoolean("include_usage", true);
            w.WriteEndObject();

            w.WritePropertyName("usage");
            w.WriteStartObject();
            w.WriteBoolean("include", true);
            w.WriteEndObject();

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
