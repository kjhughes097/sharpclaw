using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace SharpClaw.Core;

/// <summary>
/// Evaluates the persona's permission policy before a tool call proceeds.
/// For <see cref="ToolPermission.Ask"/> tools, emits a <see cref="PermissionRequestEvent"/>
/// and awaits an external decision via <see cref="Resolve"/>.
/// </summary>
public sealed class AsyncPermissionGate
{
    private readonly IReadOnlyList<(Regex Pattern, ToolPermission Permission)> _rules;
    private readonly ConcurrentDictionary<string, Channel<bool>> _pending = new();

    public AsyncPermissionGate(IReadOnlyDictionary<string, ToolPermission> policy)
    {
        _rules = policy.Select(kvp =>
        (
            new Regex(
                "^" + Regex.Escape(kvp.Key).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant),
            kvp.Value
        )).ToList();
    }

    /// <summary>
    /// Evaluates the policy for the given tool call.
    /// For <see cref="ToolPermission.Ask"/>, emits a permission request event to
    /// <paramref name="eventSink"/> and blocks until <see cref="Resolve"/> is called.
    /// </summary>
    public async Task<bool> EvaluateAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        Action<AgentEvent>? eventSink,
        CancellationToken ct)
    {
        var permission = ResolvePermission(toolName);

        return permission switch
        {
            ToolPermission.AutoApprove => true,
            ToolPermission.Deny => Denied(toolName),
            ToolPermission.Ask => await PromptAsync(toolName, arguments, eventSink, ct),
            _ => Denied(toolName),
        };
    }

    /// <summary>
    /// Resolves a pending permission request. Called from the HTTP permission endpoint.
    /// </summary>
    public bool Resolve(string requestId, bool allowed)
    {
        if (!_pending.TryRemove(requestId, out var channel))
            return false;

        channel.Writer.TryWrite(allowed);
        channel.Writer.TryComplete();
        return true;
    }

    /// <summary>
    /// Returns true if there is a pending permission request with the given ID.
    /// </summary>
    public bool HasPending(string requestId) => _pending.ContainsKey(requestId);

    private ToolPermission ResolvePermission(string toolName)
    {
        // Iterate rules in priority order (first match wins), trying all
        // candidate forms of the tool name against each rule. This ensures
        // specific rules like 'filesystem.write_*' match before a '*' catch-all.
        var candidates = GetCandidates(toolName).ToList();
        foreach (var (pattern, permission) in _rules)
        {
            foreach (var candidate in candidates)
            {
                if (pattern.IsMatch(candidate))
                    return permission;
            }
        }
        return ToolPermission.Deny;
    }

    private static IEnumerable<string> GetCandidates(string toolName)
    {
        yield return toolName;

        // Tool names use '-' as the namespace separator (e.g. filesystem-write_file)
        // but permission policies use '.' (e.g. filesystem.write_*). Yield a
        // dot-normalized form so patterns match correctly.
        var hyphenIndex = toolName.IndexOf('-');
        if (hyphenIndex > 0 && hyphenIndex < toolName.Length - 1)
        {
            var dotNormalized = string.Concat(toolName.AsSpan(0, hyphenIndex), ".", toolName.AsSpan(hyphenIndex + 1));
            yield return dotNormalized;
            yield return toolName[(hyphenIndex + 1)..];
        }

        var dotIndex = toolName.IndexOf('.');
        if (dotIndex > 0 && dotIndex < toolName.Length - 1)
            yield return toolName[(dotIndex + 1)..];
    }

    private static bool Denied(string toolName)
    {
        Console.Error.WriteLine($"⛔ Tool '{toolName}' blocked by permission policy.");
        return false;
    }

    private async Task<bool> PromptAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        Action<AgentEvent>? eventSink,
        CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N")[..8];
        var channel = Channel.CreateBounded<bool>(1);
        _pending[requestId] = channel;

        // Emit the permission request event so the SSE client can surface it.
        eventSink?.Invoke(new PermissionRequestEvent(toolName, arguments, requestId));

        try
        {
            // Wait for the external decision.
            return await channel.Reader.ReadAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }
}
