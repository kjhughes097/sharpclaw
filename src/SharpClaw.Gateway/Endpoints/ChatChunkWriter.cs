using System.Text;
using System.Text.Json;

namespace SharpClaw.Gateway.Endpoints;

/// <summary>Builds OpenAI-style chat.completion.chunk SSE lines from scratch.</summary>
public sealed class ChatChunkWriter
{
    private readonly string _id;
    private readonly string _model;

    public ChatChunkWriter(string model)
    {
        _id = $"chatcmpl-{Guid.CreateVersion7():N}";
        _model = model;
    }

    public string RoleChunk() => Line(w =>
    {
        WriteEnvelope(w, choices =>
        {
            choices.WriteStartObject();
            choices.WriteNumber("index", 0);
            choices.WritePropertyName("delta");
            choices.WriteStartObject();
            choices.WriteString("role", "assistant");
            choices.WriteEndObject();
            choices.WriteNull("finish_reason");
            choices.WriteEndObject();
        });
    });

    public string ContentChunk(string content) => Line(w =>
    {
        WriteEnvelope(w, choices =>
        {
            choices.WriteStartObject();
            choices.WriteNumber("index", 0);
            choices.WritePropertyName("delta");
            choices.WriteStartObject();
            choices.WriteString("content", content);
            choices.WriteEndObject();
            choices.WriteNull("finish_reason");
            choices.WriteEndObject();
        });
    });

    public string FinishChunk(string finishReason) => Line(w =>
    {
        WriteEnvelope(w, choices =>
        {
            choices.WriteStartObject();
            choices.WriteNumber("index", 0);
            choices.WritePropertyName("delta");
            choices.WriteStartObject();
            choices.WriteEndObject();
            choices.WriteString("finish_reason", finishReason);
            choices.WriteEndObject();
        });
    });

    public string UsageChunk(int promptTokens, int completionTokens) => Line(w =>
    {
        w.WriteStartObject();
        w.WriteString("id", _id);
        w.WriteString("object", "chat.completion.chunk");
        w.WriteString("model", _model);
        w.WritePropertyName("choices");
        w.WriteStartArray();
        w.WriteEndArray();
        w.WritePropertyName("usage");
        w.WriteStartObject();
        w.WriteNumber("prompt_tokens", promptTokens);
        w.WriteNumber("completion_tokens", completionTokens);
        w.WriteNumber("total_tokens", promptTokens + completionTokens);
        w.WriteEndObject();
        w.WriteEndObject();
    });

    public static string ErrorChunk(string code, string message) => Line(w =>
    {
        w.WriteStartObject();
        w.WritePropertyName("error");
        w.WriteStartObject();
        w.WriteString("code", code);
        w.WriteString("message", message);
        w.WriteEndObject();
        w.WriteEndObject();
    });

    public const string DoneLine = "data: [DONE]\n\n";

    private void WriteEnvelope(Utf8JsonWriter w, Action<Utf8JsonWriter> writeChoice)
    {
        w.WriteStartObject();
        w.WriteString("id", _id);
        w.WriteString("object", "chat.completion.chunk");
        w.WriteString("model", _model);
        w.WritePropertyName("choices");
        w.WriteStartArray();
        writeChoice(w);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static string Line(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
            write(w);
        return "data: " + Encoding.UTF8.GetString(ms.ToArray()) + "\n\n";
    }
}
