using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace SharpClaw.Api.Services;

public sealed class JwtTokenService
{
    private const string Issuer = "sharpclaw";
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(12);
    private readonly byte[] _secret;

    public JwtTokenService(IConfiguration configuration)
    {
        var secret = configuration["Auth:JwtSecret"]
            ?? Environment.GetEnvironmentVariable("SHARPCLAW_JWT_SECRET");

        if (string.IsNullOrWhiteSpace(secret) || secret.Trim().Length < 32)
        {
            throw new InvalidOperationException(
                "JWT secret is not configured. Set Auth:JwtSecret or SHARPCLAW_JWT_SECRET to a random string with at least 32 characters.");
        }

        _secret = Encoding.UTF8.GetBytes(secret.Trim());
    }

    public string IssueToken(string username)
        => IssueTokenWithLifetime(username, DefaultLifetime).Token;

    public (string Token, DateTimeOffset ExpiresAt) IssueTokenWithLifetime(
        string username,
        TimeSpan lifetime,
        IEnumerable<Claim>? additionalClaims = null)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username is required.", nameof(username));

        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Token lifetime must be greater than zero.");

        var now = DateTime.UtcNow;
        var expiresAt = now.Add(lifetime);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, username),
            new(JwtRegisteredClaimNames.UniqueName, username),
            new(ClaimTypes.Name, username),
        };

        if (additionalClaims is not null)
            claims.AddRange(additionalClaims);

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Issuer,
            claims: claims,
            notBefore: now,
            expires: expiresAt,
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(_secret), SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), new DateTimeOffset(expiresAt, TimeSpan.Zero));
    }

    public ClaimsPrincipal? Validate(string token)
    {
        var validation = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Issuer,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(_secret),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = ClaimTypes.Name,
        };

        var handler = new JwtSecurityTokenHandler();

        try
        {
            return handler.ValidateToken(token, validation, out _);
        }
        catch
        {
            return null;
        }
    }
}
