using System.Text.Json;
using SharpClaw.Contracts;

namespace SharpClaw.Sandbox.Ops;

/// <summary>One sandbox operation (a broker tool's sandbox half). Arguments are untrusted model output.</summary>
public interface IRunnerOp
{
    string Name { get; }
    Task<OpResult> RunAsync(JsonElement args, CancellationToken ct);
}

/// <summary>The tool result returned to the gateway: <c>{ "ok": true, ... }</c> or <c>{ "ok": false, "error", "message" }</c>.</summary>
public sealed record OpResult(bool Ok, string Json)
{
    public static OpResult Success(object payload)
    {
        var element = JsonSerializer.SerializeToElement(payload, SharpClawJson.Options);
        var fields = new Dictionary<string, object?> { ["ok"] = true };
        foreach (var p in element.EnumerateObject())
            fields[p.Name] = p.Value;
        return new OpResult(true, JsonSerializer.Serialize(fields, SharpClawJson.Options));
    }

    public static OpResult Error(string code, string message, object? extra = null)
    {
        var fields = new Dictionary<string, object?> { ["ok"] = false, ["error"] = code, ["message"] = message };
        if (extra is not null)
            foreach (var p in JsonSerializer.SerializeToElement(extra, SharpClawJson.Options).EnumerateObject())
                fields[p.Name] = p.Value;
        return new OpResult(false, JsonSerializer.Serialize(fields, SharpClawJson.Options));
    }
}

internal static class OpArgs
{
    public static string? String(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    /// <summary>A boolean argument; absent or null is <paramref name="fallback"/>, anything else non-boolean is invalid.</summary>
    public static bool? Bool(JsonElement args, string name, bool fallback = false)
    {
        if (!args.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null)
            return fallback;
        return el.ValueKind is JsonValueKind.True or JsonValueKind.False ? el.GetBoolean() : null;
    }

    /// <summary>An integer argument within a range; absent is <paramref name="fallback"/>, out of range or non-integer is invalid.</summary>
    public static int? Int(JsonElement args, string name, int fallback, int min, int max)
    {
        if (!args.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null)
            return fallback;
        return el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v) && v >= min && v <= max ? v : null;
    }
}
