using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Gateway.Services.Broker;

/// <summary>
/// Forwards sandbox tools to the calling agent's own runner (spec sections 4 and 6): the URL and
/// service secret come from the agent's gateway config, never from the request. The runner returns
/// the tool result object, which goes back to the agent as it is (within the broker's size cap).
/// </summary>
public sealed class SandboxToolHandler(IHttpClientFactory httpFactory, IOptions<ToolBrokerOptions> options) : IBrokerToolHandler
{
    public const string HttpClientName = "sandbox";

    /// <summary>The sandbox tools served so far; each later task adds its own.</summary>
    public IReadOnlyCollection<string> Tools { get; } = [BrokerTools.HttpFetch, BrokerTools.DownloadToWorkspace, BrokerTools.ExtractText];

    public async Task<BrokerResult> InvokeAsync(BrokerInvocation call, CancellationToken ct)
    {
        if (call.Config.SandboxUrl is not { Length: > 0 } baseUrl || call.Config.SandboxSecret is not { Length: > 0 } secret)
            return BrokerResult.Unavailable("This agent has no sandbox configured.");

        var host = TargetHost(call);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/ops/{call.Tool}")
        {
            Content = new StringContent(call.Arguments.GetRawText(), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeoutFor(call));
        HttpResponseMessage response;
        try
        {
            response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return BrokerResult.Refused(502, ErrorCodes.ToolFailed, "The sandbox did not answer.", host);
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
                return BrokerResult.Refused(502, ErrorCodes.ToolFailed, $"The sandbox refused the call ({(int)response.StatusCode}).", host);

            // Read one byte past the cap so an oversized answer is refused rather than cut.
            var max = options.Value.MaxResponseBytes;
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[max + 1];
            var read = await body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, timeout.Token);
            if (read > max)
                return BrokerResult.Refused(502, ErrorCodes.ToolFailed, "The tool's result was too large to return.", host);

            try
            {
                using var doc = JsonDocument.Parse(buffer.AsMemory(0, read));
                return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("ok", out _)
                    ? BrokerResult.Of(doc.RootElement.Clone(), host)
                    : BrokerResult.Refused(502, ErrorCodes.ToolFailed, "The sandbox gave an unexpected answer.", host);
            }
            catch (JsonException)
            {
                return BrokerResult.Refused(502, ErrorCodes.ToolFailed, "The sandbox gave an unexpected answer.", host);
            }
        }
    }

    /// <summary>The host a network tool reaches, for the audit only (the runner and proxy enforce the rules).</summary>
    private static string? TargetHost(BrokerInvocation call) =>
        call.Arguments.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String &&
        ToolUrls.Parse(url.GetString()).Url is { } parsed
            ? parsed.Host
            : null;

    /// <summary>A little longer than the runner's own limit for the operation, so its timeout answers first.</summary>
    private static TimeSpan TimeoutFor(BrokerInvocation call) => call.Tool switch
    {
        BrokerTools.HttpFetch or BrokerTools.ExtractText => TimeSpan.FromSeconds(90),
        _ => TimeSpan.FromMinutes(11),
    };
}
