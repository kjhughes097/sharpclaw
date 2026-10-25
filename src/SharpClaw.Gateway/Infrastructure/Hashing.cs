using System.Security.Cryptography;
using System.Text;

namespace SharpClaw.Gateway.Infrastructure;

public static class Hashing
{
    /// <summary>Lowercase hex SHA-256 of the UTF-8 bytes of <paramref name="value"/>.</summary>
    public static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Constant-time comparison of two strings via SHA-256 (so lengths always match).</summary>
    public static bool FixedTimeEquals(string a, string b)
    {
        var ha = SHA256.HashData(Encoding.UTF8.GetBytes(a));
        var hb = SHA256.HashData(Encoding.UTF8.GetBytes(b));
        return CryptographicOperations.FixedTimeEquals(ha, hb);
    }
}

public sealed record GeneratedToken(string Value, string Hash, string Prefix);

public static class TokenGenerator
{
    /// <summary>Format: sc_ + base64url(no padding) of 32 random bytes. Value is shown once.</summary>
    public static GeneratedToken Create()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var body = Base64UrlNoPadding(bytes);
        var value = "sc_" + body;
        var hash = Hashing.Sha256Hex(value);
        var prefix = value[..8];
        return new GeneratedToken(value, hash, prefix);
    }

    private static string Base64UrlNoPadding(ReadOnlySpan<byte> bytes)
    {
        var s = Convert.ToBase64String(bytes);
        return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
