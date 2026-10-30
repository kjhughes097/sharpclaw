using System.Net;
using System.Net.Sockets;

namespace SharpClaw.Gateway.Egress;

/// <summary>DNS for the egress proxy. A seam so tests can return chosen addresses (including a mix of public and private).</summary>
public interface IEgressResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

/// <summary>
/// Opens the outbound connection to an already-checked address. A seam so tests can point the
/// proxy's port 80/443 connections at a loopback test server and see which address was used.
/// </summary>
public interface IEgressConnector
{
    Task<Socket> ConnectAsync(IPEndPoint endpoint, CancellationToken ct);
}

public sealed class DnsEgressResolver : IEgressResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) =>
        Dns.GetHostAddressesAsync(host, ct);
}

public sealed class SocketEgressConnector : IEgressConnector
{
    public async Task<Socket> ConnectAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
