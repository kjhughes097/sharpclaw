using Microsoft.AspNetCore.Mvc;
using SharpClaw.Api.Models;
using SharpClaw.Api.Services;

namespace SharpClaw.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(AuthService authService) : ControllerBase
{
    private const string CookieName = "sharpclaw_auth";

    [HttpGet("status")]
    [ProducesResponseType<AuthStatusResponse>(StatusCodes.Status200OK)]
    public IActionResult Status()
        => Ok(new AuthStatusResponse(authService.IsConfigured()));

    [HttpPost("setup")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    public IActionResult Setup([FromBody] SetupAuthRequest request)
    {
        var response = authService.Setup(request);
        if (response.Payload is LoginResponse login)
            SetAuthCookie(login.Token);

        return StatusCode(response.StatusCode, response.Payload);
    }

    [HttpPost("login")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        var response = authService.Login(request);
        if (response.Payload is LoginResponse login)
            SetAuthCookie(login.Token);

        return StatusCode(response.StatusCode, response.Payload);
    }

    [HttpPost("logout")]
    [ProducesResponseType<LogoutResponse>(StatusCodes.Status200OK)]
    public IActionResult Logout()
    {
        Response.Cookies.Delete(CookieName);
        return Ok(new LogoutResponse(true));
    }

    [HttpGet("me")]
    [ProducesResponseType<AuthUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status401Unauthorized)]
    public IActionResult Me()
    {
        var username = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username))
            return Unauthorized(new ErrorResponse("Not authenticated."));

        return Ok(new AuthUserResponse(username));
    }

    private void SetAuthCookie(string token)
    {
        Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            MaxAge = TimeSpan.FromHours(12),
            IsEssential = true,
        });
    }
}
