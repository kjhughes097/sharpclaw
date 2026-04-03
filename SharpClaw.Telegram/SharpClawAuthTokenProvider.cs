namespace SharpClaw.Telegram;

public sealed class SharpClawAuthTokenProvider
{
    private readonly string _apiToken;

    public SharpClawAuthTokenProvider(string apiUrl, string apiToken)
    {
        _ = apiUrl;
        _apiToken = apiToken;
    }

    public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(_apiToken);
    }

    public void InvalidateToken()
    {
        // Static token mode does not support token refresh.
    }
}
