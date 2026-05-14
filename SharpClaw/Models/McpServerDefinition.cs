namespace SharpClaw.Models;

public sealed record McpServerDefinition
{
    public required string Transport { get; init; } // "stdio" or "http"

    // Stdio fields
    public string? Command { get; init; }
    public IReadOnlyList<string>? Args { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    // Http fields
    public string? Url { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}
