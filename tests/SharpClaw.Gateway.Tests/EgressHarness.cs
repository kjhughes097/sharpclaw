using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpClaw.Gateway.Egress;

namespace SharpClaw.Gateway.Tests;

/// <summary>
/// A gateway with the egress listener on, a fake resolver (names → chosen addresses) and a connector
/// that sends every checked connection to a loopback upstream server, recording which address the
/// proxy asked for. The blocked-range policy stays real: test names resolve to public addresses.
/// </summary>
public sealed class EgressHarness : IAsyncDisposable
{
    public const string Sandbox = "cody";
    public const string Secret = "proxy-secret-for-cody-0123";
    public static readonly IPAddress PublicIp = IPAddress.Parse("93.184.216.34");

    public GatewayFixture Fx { get; private set; } = null!;
    public int Port { get; private set; }
    public FakeResolver Resolver { get; } = new();
    public RedirectingConnector Connector { get; }
    public Upstream Server { get; } = new();
    public ConcurrentQueue<string> Logs { get; } = new();

    private EgressHarness() => Connector = new RedirectingConnector(Server);

    public static async Task<EgressHarness> StartAsync(Dictionary<string, string?>? extra = null)
    {
        var h = new EgressHarness();
        h.Port = GatewayFixture.FreePort();
        var config = new Dictionary<string, string?>
        {
            ["Egress:Port"] = h.Port.ToString(),
            [$"Egress:Sandboxes:{Sandbox}:Secret"] = Secret,
        };
        foreach (var kv in extra ?? new())
            config[kv.Key] = kv.Value;
        h.Resolver.Names["public.test"] = [PublicIp];
        h.Resolver.Names["other.test"] = [IPAddress.Parse("1.1.1.1")];
        h.Resolver.Names["mixed.test"] = [PublicIp, IPAddress.Parse("10.0.0.5")];
        h.Resolver.Names["private.test"] = [IPAddress.Parse("192.168.1.10")];
        h.Resolver.Names["localhost"] = [IPAddress.Loopback];
        h.Fx = await GatewayFixture.CreateAsync(extraConfig: config, configure: b =>
        {
            b.Services.AddSingleton<IEgressResolver>(h.Resolver);
            b.Services.AddSingleton<IEgressConnector>(h.Connector);
            b.Logging.AddProvider(new TelemetryAndAbortTests.CapturingLoggerProvider(h.Logs));
        });
        return h;
    }

    public static string Basic(string user, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    public static string ProxyAuth => $"Proxy-Authorization: {Basic("sbx-" + Sandbox, Secret)}\r\n";

    /// <summary>A raw TCP client on the egress port.</summary>
    public async Task<ProxyClient> ConnectAsync()
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, Port);
        return new ProxyClient(tcp);
    }

    /// <summary>Sends one head and returns the proxy's status line and headers (until the blank line).</summary>
    public async Task<string> SendAsync(string head)
    {
        using var client = await ConnectAsync();
        await client.WriteAsync(head);
        return await client.ReadHeadAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Fx.DisposeAsync();
        Server.Dispose();
    }

    public sealed class FakeResolver : IEgressResolver
    {
        public Dictionary<string, IPAddress[]> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentQueue<string> Calls { get; } = new();

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            Calls.Enqueue(host);
            return Names.TryGetValue(host, out var addresses)
                ? Task.FromResult(addresses)
                : Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound));
        }
    }

    public sealed class RedirectingConnector(Upstream server) : IEgressConnector
    {
        public ConcurrentQueue<IPEndPoint> Calls { get; } = new();

        public async Task<Socket> ConnectAsync(IPEndPoint endpoint, CancellationToken ct)
        {
            Calls.Enqueue(endpoint);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port), ct);
            return socket;
        }
    }

    /// <summary>
    /// Loopback upstream. <see cref="Echo"/>: echoes bytes back (a TLS stand-in). Otherwise reads one
    /// head, answers 200 "hello" and closes.
    /// </summary>
    public sealed class Upstream : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public Upstream()
        {
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public bool Echo { get; set; }
        public ConcurrentQueue<string> Received { get; } = new();
        public int Connections;

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }
                Interlocked.Increment(ref Connections);
                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            var stream = client.GetStream();
            var buffer = new byte[64 * 1024];
            var all = new StringBuilder();
            try
            {
                while (true)
                {
                    var n = await stream.ReadAsync(buffer, _stop.Token);
                    if (n == 0)
                        break;
                    var text = Encoding.Latin1.GetString(buffer, 0, n);
                    all.Append(text);
                    if (Echo)
                        await stream.WriteAsync(buffer.AsMemory(0, n), _stop.Token);
                    else if (all.ToString().Contains("\r\n\r\n"))
                    {
                        // Like a real server honouring "Connection: close": answer, then hang up.
                        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nhello"u8.ToArray(), _stop.Token);
                        break;
                    }
                }
            }
            catch { }
            finally
            {
                Received.Enqueue(all.ToString());
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    public sealed class ProxyClient(TcpClient tcp) : IDisposable
    {
        public NetworkStream Stream { get; } = tcp.GetStream();
        public Socket Socket => tcp.Client;

        public Task WriteAsync(string text) => Stream.WriteAsync(Encoding.Latin1.GetBytes(text)).AsTask();

        public async Task<string> ReadHeadAsync()
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await Stream.ReadAsync(one, timeout.Token) == 0)
                    break;
                sb.Append((char)one[0]);
            }
            return sb.ToString();
        }

        /// <summary>Reads until the proxy closes the connection.</summary>
        public async Task<string> ReadToEndAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var buffer = new byte[8192];
            var sb = new StringBuilder();
            int n;
            while ((n = await Stream.ReadAsync(buffer, timeout.Token)) > 0)
                sb.Append(Encoding.Latin1.GetString(buffer, 0, n));
            return sb.ToString();
        }

        public async Task<string> ReadExactlyAsync(int count)
        {
            var buffer = new byte[count];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Stream.ReadExactlyAsync(buffer, timeout.Token);
            return Encoding.Latin1.GetString(buffer);
        }

        public void Dispose() => tcp.Dispose();
    }
}
