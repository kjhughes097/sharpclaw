using System.Security.Cryptography;
using System.Text;

namespace SharpClaw.Gateway.Auth;

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashes in the form
/// <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;salt b64&gt;$&lt;hash b64&gt;</c> (16-byte salt, 32-byte hash).
/// </summary>
public static class PasswordHasher
{
    private const string Scheme = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public const int MinLength = 12;
    public const int MaxLength = 256;

    public static string Hash(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, iterations);
        return $"{Scheme}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Constant-time verification. <paramref name="iterations"/> is the stored count (for re-hashing).</summary>
    public static bool Verify(string password, string stored, out int iterations)
    {
        iterations = 0;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out iterations) || iterations <= 0)
            return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Null when acceptable; otherwise the policy message (never echoes the password).</summary>
    public static string? CheckPolicy(string? password) =>
        password is { Length: >= MinLength and <= MaxLength }
            ? null
            : $"password must be {MinLength} to {MaxLength} characters.";

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
}
