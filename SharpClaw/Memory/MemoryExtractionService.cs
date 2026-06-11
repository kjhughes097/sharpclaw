using System.Text.Json;
using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SharpClaw.Configuration;

namespace SharpClaw.Memory;

/// <summary>
/// Extracts structured facts, decisions, and preferences from agent exchanges
/// using a cheap LLM call, then stores them as semantic memories.
/// </summary>
public sealed class MemoryExtractionService(
    IOptions<AnthropicOptions> anthropicOptions,
    IOptions<SemanticMemoryOptions> memoryOptions,
    SemanticMemoryService memoryService,
    ILogger<MemoryExtractionService> logger)
{
    private readonly SemanticMemoryOptions _options = memoryOptions.Value;

    private const string ExtractionPrompt = """
        You are a memory extraction system. Analyse the following exchange between a user and an AI agent.
        Extract important facts, decisions, preferences, and learnings that would be useful to remember for future conversations.

        Rules:
        - Only extract genuinely important, reusable information
        - Skip transient details (timestamps, greetings, routine confirmations)
        - Skip information that is only relevant to the immediate task at hand
        - Each extracted memory should be a self-contained statement
        - Categorise each as: fact, decision, preference, or learning
        - Return JSON array only, no other text

        Output format (JSON array):
        [{"content": "...", "type": "fact|decision|preference|learning"}]

        If nothing worth remembering, return: []

        Exchange:
        USER: {0}
        AGENT: {1}
        """;

    public async Task ExtractAndStoreAsync(
        string userPrompt,
        string agentResponse,
        string agentName,
        CancellationToken ct = default)
    {
        if (!_options.ExtractionEnabled) return;
        if (userPrompt.Length < _options.MinPromptLengthForExtraction) return;
        if (agentResponse.Length < _options.MinResponseLengthForExtraction) return;

        try
        {
            var extracted = await ExtractMemoriesAsync(userPrompt, agentResponse, ct);
            if (extracted.Count == 0) return;

            logger.LogDebug("Extracted {Count} memories from exchange for agent {Agent}", extracted.Count, agentName);

            foreach (var memory in extracted)
            {
                await memoryService.StoreAsync(memory.Content, agentName, memory.Type, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Memory extraction failed for agent {Agent}", agentName);
        }
    }

    private async Task<IReadOnlyList<ExtractedMemory>> ExtractMemoriesAsync(
        string userPrompt,
        string agentResponse,
        CancellationToken ct)
    {
        // Truncate long exchanges to avoid excessive extraction costs
        var truncatedPrompt = Truncate(userPrompt, 2000);
        var truncatedResponse = Truncate(agentResponse, 3000);

        var prompt = string.Format(ExtractionPrompt, truncatedPrompt, truncatedResponse);

        var client = new AnthropicClient { ApiKey = anthropicOptions.Value.ApiKey };
        IChatClient chatClient = client.AsIChatClient(_options.ExtractionModel, _options.ExtractionMaxTokens);

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.User, prompt)
            };

            var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct);
            var text = response.Text?.Trim() ?? "[]";

            return ParseExtractedMemories(text);
        }
        finally
        {
            if (chatClient is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }
    }

    private static IReadOnlyList<ExtractedMemory> ParseExtractedMemories(string json)
    {
        // Strip markdown code fences if present
        if (json.StartsWith("```"))
        {
            var firstNewline = json.IndexOf('\n');
            var lastFence = json.LastIndexOf("```");
            if (firstNewline >= 0 && lastFence > firstNewline)
                json = json[(firstNewline + 1)..lastFence].Trim();
        }

        if (string.IsNullOrWhiteSpace(json) || json == "[]")
            return [];

        try
        {
            var items = JsonSerializer.Deserialize<List<ExtractedMemoryJson>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (items is null) return [];

            return items
                .Where(i => !string.IsNullOrWhiteSpace(i.Content))
                .Select(i => new ExtractedMemory(i.Content!, ParseType(i.Type)))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static MemoryType ParseType(string? type) => type?.ToLowerInvariant() switch
    {
        "decision" => MemoryType.Decision,
        "preference" => MemoryType.Preference,
        "learning" => MemoryType.Learning,
        _ => MemoryType.Fact
    };

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    private sealed record ExtractedMemory(string Content, MemoryType Type);

    private sealed class ExtractedMemoryJson
    {
        public string? Content { get; set; }
        public string? Type { get; set; }
    }
}
