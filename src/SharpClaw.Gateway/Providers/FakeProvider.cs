using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Gateway.Providers;

/// <summary>
/// A deterministic, offline provider for tests: echoes the last user message, supports a JSON
/// script of rules (responses, chunking, delays, usage and simulated errors). Throws
/// <see cref="ProviderException"/> exactly as the real provider would.
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

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ProviderChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var lastUser = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        var totalPromptChars = request.Messages.Sum(m => m.Content.Length);
        var rule = _script.Rules.FirstOrDefault(r => r.Match.IsMatch(lastUser));

        var response = rule?.Response ?? $"Fake reply to: {lastUser}";
        var chunkSize = Math.Max(1, rule?.ChunkSize ?? 8);
        var delayMs = rule?.DelayMs ?? 0;

        // Errors that occur before any streaming.
        if (rule?.Error is { Type: FakeErrorType.Http } httpErr)
            throw new ProviderException(ProviderErrorKind.Upstream, httpErr.Status ?? 500, "fake http error");
        if (rule?.Error is { Type: FakeErrorType.Timeout })
            throw new ProviderException(ProviderErrorKind.Timeout, null, "fake timeout");

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

        // Usage.
        if (rule is null)
        {
            // Default usage.
            var prompt = CeilDiv(totalPromptChars, 4);
            var completion = CeilDiv(response.Length, 4);
            var cost = (prompt + completion) * _costPerToken;
            yield return new ChatStreamEvent.Usage(prompt, completion, cost);
        }
        else if (!rule.UsageProvided)
        {
            var prompt = CeilDiv(totalPromptChars, 4);
            var completion = CeilDiv(response.Length, 4);
            var cost = (prompt + completion) * _costPerToken;
            yield return new ChatStreamEvent.Usage(prompt, completion, cost);
        }
        else if (rule.Usage is { } usage)
        {
            var cost = usage.CostUsd ?? (usage.PromptTokens + usage.CompletionTokens) * _costPerToken;
            yield return new ChatStreamEvent.Usage(usage.PromptTokens, usage.CompletionTokens, cost);
        }
        // else: usage explicitly null -> emit no Usage event (usage_known = false).

        yield return new ChatStreamEvent.Done("stop");
    }

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;
}
