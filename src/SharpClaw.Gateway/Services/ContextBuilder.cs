using System.Text.Json;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Services;

/// <summary>Seam for summarising turns dropped from the context. Not implemented in this phase.</summary>
public interface IContextSummariser
{
    /// <summary>Returns a summary of the dropped messages, or null to use the generic note.</summary>
    Task<string?> SummariseAsync(Guid threadId, IReadOnlyList<ContextMessage> droppedMessages, CancellationToken ct);
}

public sealed class NoSummariser : IContextSummariser
{
    public Task<string?> SummariseAsync(Guid threadId, IReadOnlyList<ContextMessage> droppedMessages, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}

/// <summary>Token estimates from character counts (pre-3.3 spec section 6.3). Never looks at content beyond its length.</summary>
public sealed class TokenEstimator(ContextOptions options)
{
    public int Estimate(ContextMessage m)
    {
        long chars = m.Content?.Length ?? 0;
        if (m.ToolCalls is { Count: > 0 })
            chars += JsonSerializer.Serialize(m.ToolCalls, SharpClawJson.Options).Length;
        return ContextDefaults.Tokens(chars, options.CharsPerToken) + options.MessageOverheadTokens;
    }

    public int Estimate(IEnumerable<ContextMessage> messages) => messages.Sum(Estimate);

    public int EstimateChars(long chars) => ContextDefaults.Tokens(chars, options.CharsPerToken);
}

/// <summary>
/// Builds the model-ready history for a turn (pre-3.3 spec section 6.4): whole turns, newest
/// first, within <c>window − reserve − reply reserve</c>; the current turn always included.
/// </summary>
public sealed class ContextBuilder(ContextOptions options, IContextSummariser summariser)
{
    public sealed record Result(
        IReadOnlyList<ContextMessage> Messages, int DroppedTurns, int EstimatedTokens, bool TooLarge);

    /// <param name="rows">All of the thread's rows.</param>
    /// <param name="currentCorrelationId">The user message being processed.</param>
    public async Task<Result> BuildAsync(
        Guid threadId, IReadOnlyList<Message> rows, Guid currentCorrelationId, int windowTokens, int reserveTokens,
        CancellationToken ct)
    {
        var estimator = new TokenEstimator(options);
        var turns = GroupTurns(rows, currentCorrelationId, estimator);
        var currentIndex = turns.FindIndex(t => t.CorrelationId == currentCorrelationId);
        if (currentIndex < 0)
            return new Result(Array.Empty<ContextMessage>(), 0, 0, false);

        var budget = windowTokens - reserveTokens - options.ReplyReserveTokens;
        var current = turns[currentIndex];
        if (current.Tokens > budget)
            return new Result(Array.Empty<ContextMessage>(), 0, current.Tokens, true);

        // Earlier turns, newest first, whole turns only; stop at the first that doesn't fit.
        var total = current.Tokens;
        var firstIncluded = currentIndex;
        for (var i = currentIndex - 1; i >= 0 && total + turns[i].Tokens <= budget; i--)
        {
            total += turns[i].Tokens;
            firstIncluded = i;
        }

        var messages = turns.Skip(firstIncluded).Take(currentIndex - firstIncluded + 1).SelectMany(t => t.Messages).ToList();
        if (firstIncluded > 0)
        {
            var dropped = turns.Take(firstIncluded).SelectMany(t => t.Messages).ToList();
            var summary = await summariser.SummariseAsync(threadId, dropped, ct);
            var note = new ContextMessage(Roles.System,
                string.IsNullOrWhiteSpace(summary) ? $"Earlier conversation ({firstIncluded} turns) is not shown." : summary);
            messages.Insert(0, note);
            total += estimator.Estimate(note);
        }

        return new Result(messages, firstIncluded, total, false);
    }

    private sealed record Turn(Guid CorrelationId, List<ContextMessage> Messages, int Tokens);

    /// <summary>
    /// A turn is a user message plus every row carrying its id as correlation id, ordered by the
    /// user message's <c>seq</c>. Grouping by correlation (not position) keeps a turn whole even if
    /// another message was queued while it ran. Turns after the current one are not included.
    /// </summary>
    private static List<Turn> GroupTurns(IReadOnlyList<Message> rows, Guid currentCorrelationId, TokenEstimator estimator)
    {
        var byCorrelation = rows
            .Where(r => !(r.Role == Roles.User && r.Kind == MessageKinds.Message) && r.CorrelationId is not null)
            .GroupBy(r => r.CorrelationId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Seq).ToList());

        var turns = new List<Turn>();
        foreach (var user in rows.Where(r => r.Role == Roles.User && r.Kind == MessageKinds.Message).OrderBy(r => r.Seq))
        {
            var turnRows = new List<Message> { user };
            if (byCorrelation.TryGetValue(user.Id, out var rest))
                turnRows.AddRange(rest);
            var messages = turnRows.Select(r => ModelReplay.ToContextMessage(r, currentCorrelationId)).ToList();
            turns.Add(new Turn(user.Id, messages, estimator.Estimate(messages)));
            if (user.Id == currentCorrelationId)
                break;
        }
        return turns;
    }
}
