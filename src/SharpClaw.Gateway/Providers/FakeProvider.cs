using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Gateway.Providers;

/// <summary>
/// A deterministic, offline provider for tests: echoes the last user message, supports a JSON
/// script of rules (responses, tool calls, chunking, delays, usage and simulated errors). Rules
/// match the request's last message, so a script can drive a multi-step tool flow: one rule matches
/// the user's text and returns a tool call, the next matches the tool result (<c>"role": "tool"</c>).
/// Throws <see cref="ProviderException"/> exactly as the real provider would.
/// </summary>
public sealed class FakeProvider : IChatProvider
{
    private readonly FakeScript _script;
    private readonly double _costPerToken;

    public FakeProvider(IOptions<FakeOptions> options)
    {
        var opt = options.Value;
        _costPerToken = opt.CostPerTokenUsd;
        _script = !string.IsNullOrWhiteSpace(opt.ScriptPath) && File.Exists(opt.ScriptPath)
            ? FakeScript.Load(opt.ScriptPath)
            : FakeScript.Empty;
    }

    // Test seam.
    public FakeProvider(FakeScript script, double costPerToken)
    {
        _script = script;
        _costPerToken = costPerToken;
    }

    /// <summary>Lists the images the request carried, so tests and a dev stack can see they arrived.</summary>
    private static string ImageNote(ProviderChatRequest request)
    {
        var images = request.Messages.SelectMany(m => m.Images ?? []).ToList();
        return images.Count == 0 ? "" : $" [images: {string.Join(", ", images.Select(i => $"{i.MediaType} {i.Data.Length} bytes"))}]";
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ProviderChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var lastUser = request.Messages.LastOrDefault(m => m.Role == Roles.User)?.Content ?? "";
        var last = request.Messages.LastOrDefault();
        var totalPromptChars = request.Messages.Sum(PromptChars);
        var rule = _script.Rules.FirstOrDefault(r =>
            last is not null &&
            (r.Role is null || r.Role == last.Role) &&
            r.Match.IsMatch(last.Content ?? ""));

        // A rule that only returns tool calls streams no text.
        var response = rule?.Response ?? (rule is { ToolCalls.Count: > 0 } ? "" : $"Fake reply to: {lastUser}{ImageNote(request)}");
        var chunkSize = Math.Max(1, rule?.ChunkSize ?? 8);
        var delayMs = rule?.DelayMs ?? 0;

        // Errors that occur before any streaming.
        if (rule?.Error is { Type: FakeErrorType.Http } httpErr)
            throw new ProviderException(ProviderErrorKind.Upstream, httpErr.Status ?? 500, "fake http error");
        if (rule?.Error is { Type: FakeErrorType.Timeout })
            throw new ProviderException(ProviderErrorKind.Timeout, null, "fake timeout");
        if (rule?.Error is { Type: FakeErrorType.ContextLength })
            throw new ProviderException(ProviderErrorKind.ContextLength, 400, "fake context length exceeded");

        var midstreamAfter = rule?.Error is { Type: FakeErrorType.Midstream } mid ? mid.AfterChars ?? 0 : (int?)null;

        var emitted = 0;
        for (var i = 0; i < response.Length; i += chunkSize)
        {
            ct.ThrowIfCancellationRequested();

            if (midstreamAfter is int after && emitted >= after)
                throw new ProviderException(ProviderErrorKind.Upstream, null, "fake midstream error");

            var remaining = response.Length - i;
            var take = Math.Min(chunkSize, remaining);

            if (midstreamAfter is int a && emitted + take > a)
                take = Math.Max(0, a - emitted);

            if (take > 0)
            {
                var piece = response.Substring(i, take);
                emitted += take;
                if (delayMs > 0)
                    await Task.Delay(delayMs, ct);
                yield return new ChatStreamEvent.Delta(piece);
            }

            if (midstreamAfter is int a2 && emitted >= a2)
                throw new ProviderException(ProviderErrorKind.Upstream, null, "fake midstream error");
        }

        var toolCalls = rule?.ToolCalls ?? Array.Empty<FakeToolCall>();
        for (var n = 0; n < toolCalls.Count; n++)
            yield return new ChatStreamEvent.ToolCall($"call_{Guid.NewGuid():N}", toolCalls[n].Name, toolCalls[n].ArgumentsJson);
        var completionChars = response.Length + toolCalls.Sum(t => t.Name.Length + t.ArgumentsJson.Length);

        // Usage.
        if (rule is null)
        {
            // Default usage.
            var prompt = CeilDiv(totalPromptChars, 4);
            var completion = CeilDiv(completionChars, 4);
            var cost = (prompt + completion) * _costPerToken;
            yield return new ChatStreamEvent.Usage(prompt, completion, cost);
        }
        else if (!rule.UsageProvided)
        {
            var prompt = CeilDiv(totalPromptChars, 4);
            var completion = CeilDiv(completionChars, 4);
            var cost = (prompt + completion) * _costPerToken;
            yield return new ChatStreamEvent.Usage(prompt, completion, cost);
        }
        else if (rule.Usage is { } usage)
        {
            var cost = usage.CostUsd ?? (usage.PromptTokens + usage.CompletionTokens) * _costPerToken;
            yield return new ChatStreamEvent.Usage(usage.PromptTokens, usage.CompletionTokens, cost);
        }
        // else: usage explicitly null -> emit no Usage event (usage_known = false).

        yield return new ChatStreamEvent.Done(toolCalls.Count > 0 ? "tool_calls" : "stop");
    }

    private static int PromptChars(ProviderChatMessage m) =>
        (m.Content?.Length ?? 0) + (m.ToolCalls?.Sum(t => t.Name.Length + t.ArgumentsJson.Length) ?? 0);

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;
}
