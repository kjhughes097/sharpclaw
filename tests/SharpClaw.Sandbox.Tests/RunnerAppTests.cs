using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using SharpClaw.Sandbox.Infrastructure;

namespace SharpClaw.Sandbox.Tests;

/// <summary>The runner accepts operations only with the gateway's service secret.</summary>
public class RunnerAppTests : IAsyncLifetime
{
    private const string Secret = "service-secret-0123456789";
    private readonly TempWorkspace _ws = new();
    private WebApplication _app = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _port = TestServer.FreePort();
        _app = RunnerApp.Build(new RunnerOptions { Secret = Secret, Port = _port, WorkspacePath = _ws.Path });
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _ws.Dispose();
    }

    private async Task<(HttpStatusCode, string)> PostAsync(string op, string body, string? token)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}/ops/{op}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    [InlineData(Secret + "x")]
    public async Task Without_the_service_secret_nothing_runs(string? token)
    {
        var (status, _) = await PostAsync("download_to_workspace", """{"url":"http://127.0.0.1:1/","path":"a.txt"}""", token);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task Unknown_operations_are_404()
    {
        var (status, _) = await PostAsync("shell", "{}", Secret);

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("not json")]
    public async Task Arguments_must_be_a_json_object(string body)
    {
        var (status, json) = await PostAsync("http_fetch", body, Secret);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("\"invalid_arguments\"", json);
    }

    [Fact]
    public async Task A_valid_call_runs()
    {
        var (status, json) = await PostAsync("http_fetch", """{"url":"ftp://x/"}""", Secret);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("\"ok\":false", json);
        Assert.Contains("invalid_arguments", json);
    }

    [Fact]
    public void A_short_secret_refuses_to_start() =>
        Assert.Throws<InvalidOperationException>(() => RunnerApp.Build(new RunnerOptions { Secret = "short", Port = TestServer.FreePort() }));
}
