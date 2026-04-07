using SharpClaw.Api.Models;
using SharpClaw.Core;

namespace SharpClaw.Api.Services;

public sealed class AuthService(SessionStore store, PasswordHashService passwordHashService, JwtTokenService jwtTokenService)
{
    public bool IsConfigured()
        => store.HasAuthUsers();

    public ApiResponse<IApiPayload> Setup(SetupAuthRequest request)
    {
        var error = ApiValidator.ValidateSetupAuthRequest(request);
        if (error is not null)
            return new ApiResponse<IApiPayload>(StatusCodes.Status400BadRequest, new ErrorResponse(error));

        if (store.HasAuthUsers())
            return new ApiResponse<IApiPayload>(StatusCodes.Status409Conflict, new ErrorResponse("Login has already been configured."));

        var username = request.Username!.Trim();
        var passwordHash = passwordHashService.HashPassword(request.Password!);
        store.CreateAuthUser(username, passwordHash);

        var token = jwtTokenService.IssueToken(username);
        return new ApiResponse<IApiPayload>(StatusCodes.Status201Created, new LoginResponse(username, token));
    }

    public ApiResponse<IApiPayload> Login(LoginRequest request)
    {
        var error = ApiValidator.ValidateLoginRequest(request);
        if (error is not null)
            return new ApiResponse<IApiPayload>(StatusCodes.Status400BadRequest, new ErrorResponse(error));

        var user = store.GetSingleAuthUser();
        if (user is null)
            return new ApiResponse<IApiPayload>(StatusCodes.Status409Conflict, new ErrorResponse("Login is not configured. Complete initial setup first."));

        var username = request.Username!.Trim();
        if (!string.Equals(user.Value.Username, username, StringComparison.Ordinal) ||
            !passwordHashService.Verify(request.Password!, user.Value.PasswordHash))
        {
            return new ApiResponse<IApiPayload>(StatusCodes.Status401Unauthorized, new ErrorResponse("Invalid username or password."));
        }

        var token = jwtTokenService.IssueToken(username);
        return new ApiResponse<IApiPayload>(StatusCodes.Status200OK, new LoginResponse(username, token));
    }
}
