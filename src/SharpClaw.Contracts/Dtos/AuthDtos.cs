using System.Text.Json.Serialization;

namespace SharpClaw.Contracts;

// ----- Browser auth and users (pre-3.3 spec section 4) -----

public sealed record LoginRequest(string Username, string Password);

public sealed record UserDto(Guid Id, string Name);

public sealed record LoginResponse(UserDto User, string CsrfToken, DateTime ExpiresAt);

/// <summary><c>Method</c> is <c>session</c> or <c>admin_key</c>; the rest is omitted when not authenticated.</summary>
public sealed record SessionInfoResponse(
    bool Authenticated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Method = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UserDto? User = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CsrfToken = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? ExpiresAt = null);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record CreateUserRequest(string Name, string Password);

public sealed record SetPasswordRequest(string Password);

public sealed record UpdateUserRequest(bool? Disabled);

/// <summary>A user as shown to the admin. Never carries the password hash.</summary>
public sealed record UserAdminDto(Guid Id, string Name, bool Disabled, DateTime CreatedAt);

public sealed record UserListResponse(IReadOnlyList<UserAdminDto> Users);

public sealed record CreatedUserResponse(UserAdminDto User);
