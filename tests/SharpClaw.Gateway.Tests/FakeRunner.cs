using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Gateway.Tests;

/// <summary>A scripted sandbox runner on loopback: records each operation and answers from <see cref="Reply"/>.</summary>
public sealed class FakeRunner : IAsyncDisposable
{
    public const string Secret = "fake-runner-secret-0123456789";
    private readonly WebApplication _app;

    public int Port { get; } = GatewayFixture.FreePort();
    public string Url => $"http://127.0.0.1:{Port}";
    public ConcurrentQueue<(string Op, JsonElement Args)> Calls { get; } = new();
    public Func<string, JsonElement, object> Reply { get; set; } = (_, _) => new { ok = true };

    public FakeRunner()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, Port));
        _app = builder.Build();
        _app.MapPost("/ops/{op}", async (string op, HttpContext http) =>
        {
            if (http.Request.Headers.Authorization != $"Bearer {Secret}")
                return Results.StatusCode(401);
            var args = (await JsonDocument.ParseAsync(http.Request.Body)).RootElement.Clone();
            Calls.Enqueue((op, args));
            return Results.Json(Reply(op, args));
        });
    }

    public static async Task<FakeRunner> StartAsync()
    {
        var runner = new FakeRunner();
        await runner._app.StartAsync();
        return runner;
    }

    /// <summary>Broker config for <paramref name="agent"/> pointing at this runner.</summary>
    public Dictionary<string, string?> Config(string agent, params string[] tools)
    {
        var config = BrokerTestHelpers.Config(agent, tools);
        config[$"ToolBroker:Agents:{agent}:SandboxUrl"] = Url;
        config[$"ToolBroker:Agents:{agent}:SandboxSecret"] = Secret;
        return config;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
