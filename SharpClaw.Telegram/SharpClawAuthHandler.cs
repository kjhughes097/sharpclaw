using System.Net;
using System.Net.Http.Headers;

namespace SharpClaw.Telegram;

public sealed class SharpClawAuthHandler(
    SharpClawAuthTokenProvider tokenProvider,
    ILogger<SharpClawAuthHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var retryRequest = await CloneHttpRequestMessageAsync(request, cancellationToken);

        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await tokenProvider.GetTokenAsync(cancellationToken));

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            retryRequest.Dispose();
            return response;
        }

        logger.LogWarning(
            "SharpClaw API returned 401 for {Method} {Uri}; refreshing token and retrying once.",
            request.Method,
            request.RequestUri);

        response.Dispose();
        tokenProvider.InvalidateToken();

        retryRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await tokenProvider.GetTokenAsync(cancellationToken));

        return await base.SendAsync(retryRequest, cancellationToken);
    }

    private static async Task<HttpRequestMessage> CloneHttpRequestMessageAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var content = new ByteArrayContent(bytes);

            foreach (var header in request.Content.Headers)
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);

            clone.Content = content;
        }

        foreach (var option in request.Options)
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);

        return clone;
    }
}
