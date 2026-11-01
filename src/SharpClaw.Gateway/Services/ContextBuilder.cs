using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>Turns loaded on the first attempt by <see cref="BuildForThreadAsync"/>; doubled while they all fit.</summary>
    public const int InitialTurnsToLoad = 50;

    /// <summary>
    /// Builds the context for <paramref name="current"/> without loading the whole thread: only the
    /// newest turns are read, and more only while every loaded turn still fits. Earlier turns can't
    /// be included once a newer one doesn't fit (whole turns, newest first), so they're just counted.
    /// </summary>
    public async Task<Result> BuildForThreadAsync(
        SharpClawDbContext db, Guid threadId, Message current, int windowTokens, int reserveTokens, CancellationToken ct)
    {
        var turnStarts = db.Messages.AsNoTracking().Where(m =>
            m.ThreadId == threadId && m.Role == Roles.User && m.Kind == MessageKinds.Message && m.Seq <= current.Seq);
        for (var take = InitialTurnsToLoad; ; take *= 2)
        {
            var seqs = await turnStarts.OrderByDescending(m => m.Seq).Select(m => m.Seq).Take(take + 1).ToListAsync(ct);
            var hasOlder = seqs.Count > take;
            // A turn's rows all come after its user message, so "seq >= the oldest loaded user message"
            // holds every loaded turn whole (plus rows of later turns, which grouping ignores).
            var fromSeq = hasOlder ? seqs[take - 1] : 0;
            var olderTurns = hasOlder ? await turnStarts.CountAsync(m => m.Seq < fromSeq, ct) : 0;
            var rows = await db.Messages.AsNoTracking()
                .Where(m => m.ThreadId == threadId && m.Seq >= fromSeq).OrderBy(m => m.Seq).ToListAsync(ct);

            var senders = await MessageQueries.SenderNamesAsync(db, rows, ct);
            var result = await BuildAsync(threadId, rows, current.Id, windowTokens, reserveTokens, ct, olderTurns, senders);
            // Done unless every loaded turn fitted and there are older ones that might fit too.
            if (!hasOlder || result.TooLarge || result.DroppedTurns > olderTurns)
                return result;
        }
    }

    /// <param name="rows">The thread's rows, from the oldest turn to consider onwards.</param>
    /// <param name="currentCorrelationId">The user message being processed.</param>
    /// <param name="olderTurns">Turns before those in <paramref name="rows"/>; they count as dropped.</param>
    /// <param name="senderNames">Names of agents that sent user messages in <paramref name="rows"/>.</param>
    public async Task<Result> BuildAsync(
        Guid threadId, IReadOnlyList<Message> rows, Guid currentCorrelationId, int windowTokens, int reserveTokens,
        CancellationToken ct, int olderTurns = 0, IReadOnlyDictionary<Guid, string>? senderNames = null)
    {
        var estimator = new TokenEstimator(options);
        var turns = GroupTurns(rows, currentCorrelationId, estimator, senderNames);
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

        int Dropped() => olderTurns + firstIncluded;

        // The note about dropped turns must fit too, or the full prompt would overshoot the window.
        while (Dropped() > 0 && firstIncluded < currentIndex &&
               total + estimator.Estimate(Note(Dropped(), null)) > budget)
        {
            total -= turns[firstIncluded].Tokens;
            firstIncluded++;
        }
        if (Dropped() > 0 && total + estimator.Estimate(Note(Dropped(), null)) > budget)
            return new Result(Array.Empty<ContextMessage>(), 0, total, true);

        var messages = turns.Skip(firstIncluded).Take(currentIndex - firstIncluded + 1).SelectMany(t => t.Messages).ToList();
        if (Dropped() > 0)
        {
            // The summariser sees the dropped turns that were loaded (older ones were never read).
            var dropped = turns.Take(firstIncluded).SelectMany(t => t.Messages).ToList();
            var note = Note(Dropped(), await summariser.SummariseAsync(threadId, dropped, ct));
            messages.Insert(0, note);
            total += estimator.Estimate(note);
        }

        return new Result(messages, Dropped(), total, false);
    }

    private static ContextMessage Note(int droppedTurns, string? summary) => new(Roles.System,
        string.IsNullOrWhiteSpace(summary) ? $"Earlier conversation ({droppedTurns} turns) is not shown." : summary);

    private sealed record Turn(Guid CorrelationId, List<ContextMessage> Messages, int Tokens);

    /// <summary>
    /// A turn is a user message plus every row carrying its id as correlation id, ordered by the
    /// user message's <c>seq</c>. Grouping by correlation (not position) keeps a turn whole even if
    /// another message was queued while it ran. Turns after the current one are not included.
    /// </summary>
    private static List<Turn> GroupTurns(
        IReadOnlyList<Message> rows, Guid currentCorrelationId, TokenEstimator estimator, IReadOnlyDictionary<Guid, string>? senderNames)
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
            var messages = turnRows.Select(r => ModelReplay.ToContextMessage(r, currentCorrelationId, senderNames)).ToList();
            turns.Add(new Turn(user.Id, messages, estimator.Estimate(messages)));
            if (user.Id == currentCorrelationId)
                break;
        }
        return turns;
    }
}
