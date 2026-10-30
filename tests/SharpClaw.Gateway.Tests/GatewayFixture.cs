using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using SharpClaw.Contracts;
using SharpClaw.Gateway;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Tests;

/// <summary>
/// Starts the real gateway on two ephemeral localhost ports (client + agent), backed by a
/// temporary SQLite file, the FakeProvider, and a controllable <see cref="FakeTimeProvider"/>.
/// </summary>
public sealed class GatewayFixture : IAsyncDisposable
{
    public const string AdminKey = "test-admin-key";

    public int ClientPort { get; }
    public int AgentPort { get; }
    public string ClientBaseUrl => $"http://localhost:{ClientPort}";
    public string AgentBaseUrl => $"http://localhost:{AgentPort}";
    public FakeTimeProvider Time { get; }
    public WebApplication App { get; }

    private readonly string _dbPath;

    private GatewayFixture(WebApplication app, FakeTimeProvider time, int clientPort, int agentPort, string dbPath)
    {
        App = app;
        Time = time;
        ClientPort = clientPort;
        AgentPort = agentPort;
        _dbPath = dbPath;
    }

    public static async Task<GatewayFixture> CreateAsync(
        FakeTimeProvider? time = null,
        IDictionary<string, string?>? extraConfig = null,
        Action<WebApplicationBuilder>? configure = null)
    {
        time ??= new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T12:00:00Z"));
        // FreePort can race with other tests in parallel: retry with fresh ports if a bind fails.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryCreateAsync(time, extraConfig, configure);
            }
            catch (IOException) when (attempt < 5)
            {
            }
        }
    }

    private static async Task<GatewayFixture> TryCreateAsync(
        FakeTimeProvider time, IDictionary<string, string?>? extraConfig, Action<WebApplicationBuilder>? configure)
    {
        var clientPort = FreePort();
        var agentPort = FreePort();
        var dbPath = Path.Combine(Path.GetTempPath(), $"sharpclaw-test-{Guid.NewGuid():N}.db");

        var config = new Dictionary<string, string?>
        {
            ["Auth:AdminKey"] = AdminKey,
            ["Provider:Type"] = "Fake",
            ["Database:Path"] = dbPath,
            ["Listeners:ClientPort"] = clientPort.ToString(),
            ["Listeners:AgentPort"] = agentPort.ToString(),
            // FakeTimeProvider fires every elapsed period on Advance; a 10 s sweep makes multi-day jumps slow.
            ["Approvals:SweepIntervalSeconds"] = "3600",
        };
        if (extraConfig is not null)
            foreach (var kv in extraConfig) config[kv.Key] = kv.Value;

        var app = GatewayBuilder.Build(Array.Empty<string>(), builder =>
        {
            builder.Configuration.AddInMemoryCollection(config);
            builder.Services.AddSingleton<TimeProvider>(time);
            configure?.Invoke(builder);
        });

        GatewayBuilder.Migrate(app);
        try
        {
            await app.StartAsync();
        }
        catch (IOException)
        {
            await app.DisposeAsync();
            throw;
        }

        return new GatewayFixture(app, time, clientPort, agentPort, dbPath);
    }

    public HttpClient ClientRaw() => new() { BaseAddress = new Uri(ClientBaseUrl) };

    public HttpClient Admin()
    {
        var c = ClientRaw();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminKey);
        return c;
    }

    public HttpClient AgentRaw() => new() { BaseAddress = new Uri(AgentBaseUrl) };

    public HttpClient AgentClient(string token)
    {
        var c = AgentRaw();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public SharpClawDbContext NewDb()
    {
        var scope = App.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
    }

    /// <summary>Creates an agent via the admin API and returns the agent + its first token value.</summary>
    public async Task<(AgentDto Agent, string Token)> CreateAgentAsync(
        string name = "assistant", string model = "test/model", double budget = 1.0)
    {
        using var admin = Admin();
        var resp = await admin.PostAsJsonAsync("/api/agents",
            new CreateAgentRequest(name, model, budget), SharpClawJson.Options);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<CreatedAgentResponse>(SharpClawJson.Options);
        return (body!.Agent, body.Token.Value);
    }

    private static readonly HashSet<int> Handed = new();

    /// <summary>
    /// An ephemeral port nobody in this test run has been given yet. The OS can hand the same free
    /// port to two lookups in a row (say, the client and agent listeners), so each is remembered.
    /// </summary>
    public static int FreePort()
    {
        while (true)
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            lock (Handed)
                if (Handed.Add(port))
                    return port;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
        }
    }
}
