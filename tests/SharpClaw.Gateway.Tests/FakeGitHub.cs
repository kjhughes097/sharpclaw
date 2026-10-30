using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using SharpClaw.Gateway.GitHub;

namespace SharpClaw.Gateway.Tests;

/// <summary>
/// Stands in for api.github.com behind both GitHub clients (the real registration still applies:
/// base address, headers, the token per client). Records every request with the token it carried.
/// </summary>
public sealed class FakeGitHub : HttpMessageHandler
{
    public const string OwnerToken = "ghp_owner_token_for_tests";
    public const string ForkToken = "ghp_fork_token_for_tests";

    public sealed record Seen(HttpMethod Method, Uri Uri, string? Token, string? Accept, string? Body);

    public ConcurrentQueue<Seen> Requests { get; } = new();

    /// <summary>Answers by "METHOD /path" (query ignored); unknown routes are 404.</summary>
    public Dictionary<string, Func<Seen, HttpResponseMessage>> Routes { get; } = new();

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    public void On(string route, object body, HttpStatusCode status = HttpStatusCode.OK) => Routes[route] = _ => Json(body, status);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var seen = new Seen(request.Method, request.RequestUri!, request.Headers.Authorization?.Parameter,
            string.Join(",", request.Headers.Accept), request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
        Requests.Enqueue(seen);
        return Routes.TryGetValue($"{request.Method} {request.RequestUri!.AbsolutePath}", out var answer)
            ? answer(seen)
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    /// <summary>Gateway config with both tokens, and the fake installed behind both clients.</summary>
    public Action<WebApplicationBuilder> Install() => b =>
    {
        foreach (var name in new[] { GitHubClient.OwnerClient, GitHubClient.ForkClient })
            b.Services.PostConfigure<HttpClientFactoryOptions>(name, o =>
                o.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = this));
    };

    public static Dictionary<string, string?> Tokens(Dictionary<string, string?> config)
    {
        config["GitHub:Token"] = OwnerToken;
        config["GitHub:ForkToken"] = ForkToken;
        return config;
    }
}
