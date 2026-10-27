using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Gateway.Auth;

/// <summary>
/// In-memory login failure counters per username and per remote IP (pre-3.3 spec section 4.4).
/// Counters reset on restart; that is accepted.
/// </summary>
public sealed class LoginRateLimiter(TimeProvider time, IOptions<AuthOptions> options)
{
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _failures = new();

    private LoginOptions Login => options.Value.Login;
    private TimeSpan Window => TimeSpan.FromMinutes(Login.WindowMinutes);

    /// <summary>When either key is over the limit, how long until the oldest failure leaves the window.</summary>
    public TimeSpan? RetryAfter(string username, string ip)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var waits = new[] { UserKey(username), IpKey(ip) }
            .Select(k => Wait(k, now))
            .Where(w => w is not null)
            .ToList();
        return waits.Count == 0 ? null : waits.Max();
    }

    public void RecordFailure(string username, string ip)
    {
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var key in new[] { UserKey(username), IpKey(ip) })
        {
            var q = _failures.GetOrAdd(key, _ => new Queue<DateTime>());
            lock (q)
            {
                Trim(q, now);
                q.Enqueue(now);
            }
        }
    }

    /// <summary>A successful login clears the username's counter (not the IP's).</summary>
    public void RecordSuccess(string username) => _failures.TryRemove(UserKey(username), out _);

    private TimeSpan? Wait(string key, DateTime now)
    {
        if (!_failures.TryGetValue(key, out var q))
            return null;
        lock (q)
        {
            Trim(q, now);
            if (q.Count < Login.MaxFailures)
                return null;
            return q.Peek() + Window - now;
        }
    }

    private void Trim(Queue<DateTime> q, DateTime now)
    {
        while (q.Count > 0 && q.Peek() + Window <= now)
            q.Dequeue();
    }

    private static string UserKey(string username) => "u:" + username.Trim().ToLowerInvariant();
    private static string IpKey(string ip) => "ip:" + ip;
}
