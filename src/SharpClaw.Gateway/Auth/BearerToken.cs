namespace SharpClaw.Gateway.Auth;

public static class BearerToken
{
    public static string? Read(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (string.IsNullOrEmpty(header) || !header.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var token = header[prefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }
}
