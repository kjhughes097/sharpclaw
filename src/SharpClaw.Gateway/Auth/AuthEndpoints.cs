using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway.Auth;

/// <summary>
/// Browser login and user management (pre-3.3 spec section 4.3). Never logs passwords, hashes,
/// cookie values, CSRF tokens, or the username submitted on a failed login.
/// </summary>
public static partial class AuthEndpoints
{
    private const string InvalidLogin = "Invalid username or password.";

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,31}$")]
    private static partial Regex UserNameRegex();

    public static void Map(RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("auth");
        auth.MapPost("/login", Login).WithName("login").Produces<LoginResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status429TooManyRequests);
        auth.MapGet("/session", SessionInfo).WithName("getSession").Produces<SessionInfoResponse>();
        auth.MapPost("/logout", Logout).WithName("logout").Produces(StatusCodes.Status204NoContent)
            .AddEndpointFilter(new ClientAuthFilter(ClientAuthMode.SessionOnly));
        auth.MapPost("/password", ChangePassword).WithName("changePassword").Produces(StatusCodes.Status204NoContent)
            .AddEndpointFilter(new ClientAuthFilter(ClientAuthMode.SessionOnly));

        // Admin-key only and not used by the UI (user creation stays a CLI action).
        var users = api.MapGroup("/users").WithTags("users")
            .AddEndpointFilter(new ClientAuthFilter(ClientAuthMode.AdminKeyOnly));
        users.MapPost("", CreateUser).WithName("createUser").Produces<CreatedUserResponse>(StatusCodes.Status201Created);
        users.MapGet("", ListUsers).WithName("listUsers").Produces<UserListResponse>();
        users.MapPut("/{name}/password", SetPassword).WithName("setUserPassword").Produces(StatusCodes.Status204NoContent);
        users.MapPatch("/{name}", UpdateUser).WithName("updateUser").Produces<UserAdminDto>();
    }

    // A fixed dummy hash per iteration count, so an unknown user costs the same work as a real one.
    private static readonly Dictionary<int, string> DummyHashes = new();

    private static string DummyHash(int iterations)
    {
        lock (DummyHashes)
        {
            if (!DummyHashes.TryGetValue(iterations, out var hash))
                DummyHashes[iterations] = hash = PasswordHasher.Hash(SessionService.RandomToken(), iterations);
            return hash;
        }
    }

    private static async Task<IResult> Login(
        LoginRequest? body, HttpContext http, SharpClawDbContext db, SessionService sessions, LoginRateLimiter limiter,
        IOptions<AuthOptions> options, GatewayMetrics metrics, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var auth = options.Value;
        var logger = loggerFactory.CreateLogger("SharpClaw.Gateway.Auth");
        if (!OriginCheck.IsAllowed(http.Request, auth.AllowedOrigins))
        {
            metrics.RecordAuthFailure("client", "origin");
            return ClientAuthFilter.CsrfFailed();
        }

        var username = body?.Username?.Trim().ToLowerInvariant() ?? "";
        var password = body?.Password ?? "";
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (limiter.RetryAfter(username, ip) is { } wait)
        {
            metrics.RecordLogin("rate_limited");
            logger.LogWarning("login rate limited");
            var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
            return ApiResults.Error(429, ErrorCodes.RateLimited, "Too many failed logins. Try again later.",
                h => h["Retry-After"] = seconds.ToString());
        }

        var user = username.Length == 0 ? null : await db.Users.FirstOrDefaultAsync(u => u.Name == username, ct);
        var iterations = auth.Password.Pbkdf2Iterations;
        // Always hash: an unknown user must take as long as a wrong password.
        var verified = PasswordHasher.Verify(password, user?.PasswordHash ?? DummyHash(iterations), out var storedIterations);
        if (user is null || !verified || user.Disabled)
        {
            limiter.RecordFailure(username, ip);
            metrics.RecordLogin("failure");
            metrics.RecordAuthFailure("client", "password");
            logger.LogWarning("login failed");
            return ApiResults.Unauthorized(InvalidLogin);
        }

        limiter.RecordSuccess(username);
        if (storedIterations < iterations)
        {
            user.PasswordHash = PasswordHasher.Hash(password, iterations);
            await db.SaveChangesAsync(ct);
        }

        var (session, cookie) = await sessions.CreateAsync(user, ct);
        sessions.SetCookie(http, cookie, session.ExpiresAt);
        metrics.RecordLogin("success");
        logger.LogInformation("login succeeded: user={User} session_id={SessionId}", user.Name, session.Id);
        return Results.Json(new LoginResponse(new UserDto(user.Id, user.Name), session.CsrfToken, session.ExpiresAt),
            SharpClawJson.Options);
    }

    private static async Task<IResult> SessionInfo(HttpContext http, SessionService sessions, IOptions<AuthOptions> options, CancellationToken ct)
    {
        var bearer = BearerToken.Read(http.Request);
        if (bearer is not null)
            return Results.Json(Hashing.FixedTimeEquals(bearer, options.Value.AdminKey)
                ? new SessionInfoResponse(true, ClientAuthMethods.AdminKey)
                : new SessionInfoResponse(false), SharpClawJson.Options);

        var session = await sessions.ResolveAsync(http.Request, ct);
        return Results.Json(session is null
            ? new SessionInfoResponse(false)
            : new SessionInfoResponse(true, ClientAuthMethods.Session, new UserDto(session.User!.Id, session.User.Name),
                session.CsrfToken, session.ExpiresAt), SharpClawJson.Options);
    }

    private static async Task<IResult> Logout(HttpContext http, SessionService sessions, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var session = ClientAuthFilter.Principal(http)!.Session!;
        await sessions.RevokeAsync(session.Id, ct);
        sessions.ClearCookie(http);
        loggerFactory.CreateLogger("SharpClaw.Gateway.Auth").LogInformation("logout: session_id={SessionId}", session.Id);
        return Results.NoContent();
    }

    private static async Task<IResult> ChangePassword(
        ChangePasswordRequest? body, HttpContext http, SharpClawDbContext db, SessionService sessions,
        IOptions<AuthOptions> options, CancellationToken ct)
    {
        var session = ClientAuthFilter.Principal(http)!.Session!;
        var user = await db.Users.FirstAsync(u => u.Id == session.UserId, ct);
        if (body is null || !PasswordHasher.Verify(body.CurrentPassword ?? "", user.PasswordHash, out _))
            return ApiResults.Unauthorized(InvalidLogin);
        if (PasswordHasher.CheckPolicy(body.NewPassword) is { } policy)
            return ApiResults.ValidationFailed(policy);

        user.PasswordHash = PasswordHasher.Hash(body.NewPassword, options.Value.Password.Pbkdf2Iterations);
        await db.SaveChangesAsync(ct);
        await sessions.RevokeAllAsync(user.Id, except: session.Id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateUser(
        CreateUserRequest? body, SharpClawDbContext db, TimeProvider time, IOptions<AuthOptions> options, CancellationToken ct)
    {
        if (body is null || string.IsNullOrEmpty(body.Name) || !UserNameRegex().IsMatch(body.Name))
            return ApiResults.ValidationFailed("name must match ^[a-z0-9][a-z0-9._-]{1,31}$.");
        if (PasswordHasher.CheckPolicy(body.Password) is { } policy)
            return ApiResults.ValidationFailed(policy);
        if (await db.Users.AnyAsync(u => u.Name == body.Name, ct))
            return ApiResults.Conflict($"User name '{body.Name}' is already taken.");

        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Name = body.Name,
            PasswordHash = PasswordHasher.Hash(body.Password, options.Value.Password.Pbkdf2Iterations),
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return ApiResults.Conflict($"User name '{body.Name}' is already taken.");
        }
        return Results.Json(new CreatedUserResponse(ToDto(user)), SharpClawJson.Options, statusCode: 201);
    }

    private static async Task<IResult> ListUsers(SharpClawDbContext db, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().OrderBy(u => u.Name).ToListAsync(ct);
        return Results.Json(new UserListResponse(users.Select(ToDto).ToList()), SharpClawJson.Options);
    }

    private static async Task<IResult> SetPassword(
        string name, SetPasswordRequest? body, SharpClawDbContext db, SessionService sessions,
        IOptions<AuthOptions> options, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Name == name, ct);
        if (user is null)
            return ApiResults.NotFound("User not found.");
        if (PasswordHasher.CheckPolicy(body?.Password) is { } policy)
            return ApiResults.ValidationFailed(policy);

        user.PasswordHash = PasswordHasher.Hash(body!.Password, options.Value.Password.Pbkdf2Iterations);
        await db.SaveChangesAsync(ct);
        await sessions.RevokeAllAsync(user.Id, except: null, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdateUser(
        string name, UpdateUserRequest? body, SharpClawDbContext db, SessionService sessions, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Name == name, ct);
        if (user is null)
            return ApiResults.NotFound("User not found.");
        if (body?.Disabled is not { } disabled)
            return ApiResults.ValidationFailed("disabled is required.");

        user.Disabled = disabled;
        await db.SaveChangesAsync(ct);
        if (disabled)
            await sessions.RevokeAllAsync(user.Id, except: null, ct);
        return Results.Json(ToDto(user), SharpClawJson.Options);
    }

    private static UserAdminDto ToDto(User u) => new(u.Id, u.Name, u.Disabled, u.CreatedAt);
}
