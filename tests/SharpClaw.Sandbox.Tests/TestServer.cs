using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Sandbox.Tests;

/// <summary>A loopback web server for fetch tests; register routes, then start.</summary>
public sealed class TestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    public TestServer(Action<WebApplication> routes)
    {
        Port = FreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, Port));
        _app = builder.Build();
        routes(_app);
    }

    public int Port { get; }
    public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

    public async Task<TestServer> StartAsync()
    {
        await _app.StartAsync();
        return this;
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static IResult Html(string html) => Results.Text(html, "text/html; charset=utf-8");

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>A temporary workspace folder, removed afterwards.</summary>
public sealed class TempWorkspace : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sbx-{Guid.NewGuid():N}");
    public TempWorkspace() => Directory.CreateDirectory(Path);
    public string File(string relative) => System.IO.Path.Combine(Path, relative);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
