using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Auth;

/// <summary>Browser sessions: creation, cookie handling, validation (idle and absolute expiry) and revocation.</summary>
public sealed class SessionService(SharpClawDbContext db, TimeProvider time, IOptions<AuthOptions> options)
{
    private SessionCookieOptions Cookie => options.Value.Session;

    public async Task<(Session Session, string CookieValue)> CreateAsync(User user, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var value = RandomToken();
        var session = new Session
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            User = user,
            TokenHash = Hashing.Sha256Hex(value),
            CsrfToken = RandomToken(),
            CreatedAt = now,
            ExpiresAt = now.AddDays(Cookie.AbsoluteTimeoutDays),
            LastSeenAt = now,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
        return (session, value);
    }

    /// <summary>The valid session for the request's cookie, or null. Slides <c>last_seen_at</c> at most once a minute.</summary>
    public async Task<Session?> ResolveAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.Cookies.TryGetValue(Cookie.CookieName, out var value) || string.IsNullOrEmpty(value))
            return null;

        var hash = Hashing.Sha256Hex(value);
        var session = await db.Sessions.Include(s => s.User).FirstOrDefaultAsync(s => s.TokenHash == hash, ct);
        var now = time.GetUtcNow().UtcDateTime;
        if (session?.User is null || session.User.Disabled || session.RevokedAt is not null || now >= session.ExpiresAt ||
            now - session.LastSeenAt >= TimeSpan.FromHours(Cookie.IdleTimeoutHours))
            return null;

        if (now - session.LastSeenAt >= TimeSpan.FromMinutes(1))
        {
            session.LastSeenAt = now;
            await db.SaveChangesAsync(ct);
        }
        return session;
    }

    public async Task RevokeAsync(Guid sessionId, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await db.Sessions.Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
    }

    /// <summary>Revokes every live session of the user, optionally keeping one (the caller's).</summary>
    public async Task RevokeAllAsync(Guid userId, Guid? except, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null && s.Id != except)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
    }

    public void SetCookie(HttpContext http, string value, DateTime expiresAt) =>
        http.Response.Cookies.Append(Cookie.CookieName, value, CookieOptions(http, expiresAt));

    public void ClearCookie(HttpContext http) =>
        http.Response.Cookies.Delete(Cookie.CookieName, CookieOptions(http, DateTime.UnixEpoch));

    private CookieOptions CookieOptions(HttpContext http, DateTime expiresAt) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Secure = Cookie.CookieSecure switch
        {
            CookieSecureMode.Always => true,
            CookieSecureMode.Never => false,
            _ => http.Request.IsHttps,
        },
        Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)),
    };

    /// <summary>32 random bytes, base64url without padding.</summary>
    public static string RandomToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
