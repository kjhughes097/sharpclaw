using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Gateway.Services.Broker;

/// <summary>
/// Calls an operation on the calling agent's own sandbox runner (spec sections 4 and 6). The URL and
/// service secret come from the agent's gateway config, never from the request. The runner answers
/// with a tool result object, returned as it is within the broker's size cap.
/// </summary>
public sealed class SandboxClient(IHttpClientFactory httpFactory, IOptions<ToolBrokerOptions> options)
{
    public const string HttpClientName = "sandbox";

    public bool IsConfigured(BrokerAgentOptions config) =>
        config.SandboxUrl is { Length: > 0 } && config.SandboxSecret is { Length: > 0 };

    /// <param name="op">The runner operation (usually the tool name).</param>
    /// <param name="targetHost">For the audit only.</param>
    public async Task<BrokerResult> CallAsync(
        BrokerAgentOptions config, string op, JsonElement arguments, TimeSpan timeout, string? targetHost, CancellationToken ct)
    {
        if (!IsConfigured(config))
            return BrokerResult.Unavailable("This agent has no sandbox configured.");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{config.SandboxUrl!.TrimEnd('/')}/ops/{op}")
        {
            Content = new StringContent(arguments.GetRawText(), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.SandboxSecret);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        HttpResponseMessage response;
        try
        {
            response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return BrokerResult.Refused(502, ErrorCodes.ToolFailed, "The sandbox did not answer.", targetHost);
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
                return BrokerResult.Refused(502, ErrorCodes.ToolFailed, $"The sandbox refused the call ({(int)response.StatusCode}).", targetHost);

            // Read one byte past the cap so an oversized answer is refused rather than cut.
            var max = options.Value.MaxResponseBytes;
            await using var body = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            var buffer = new byte[max + 1];
            var read = await body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, timeoutCts.Token);
            if (read > max)
                return BrokerResult.Refused(502, ErrorCodes.ToolFailed, "The tool's result was too large to return.", targetHost);

            try
            {
                using var doc = JsonDocument.Parse(buffer.AsMemory(0, read));
                return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("ok", out _)
                    ? BrokerResult.Of(doc.RootElement.Clone(), targetHost)
                    : BrokerResult.Refused(502, ErrorCodes.ToolFailed, "The sandbox gave an unexpected answer.", targetHost);
            }
            catch (JsonException)
            {
                return BrokerResult.Refused(502, ErrorCodes.ToolFailed, "The sandbox gave an unexpected answer.", targetHost);
            }
        }
    }
}
