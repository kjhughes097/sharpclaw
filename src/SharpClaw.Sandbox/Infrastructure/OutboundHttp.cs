using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace SharpClaw.Sandbox.Infrastructure;

/// <summary>Outbound HTTP for the fetch operations. A seam so tests can serve pages from loopback.</summary>
public interface IOutboundHttp
{
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct);
}

/// <summary>
/// Sends through the gateway's egress proxy with the sandbox's credential, presented up front:
/// <c>https</c> through a <c>CONNECT</c> tunnel the handler opens itself, plain <c>http</c> in
/// absolute form. Redirects are never followed here; callers follow them one hop at a time, so the
/// proxy checks every hop.
/// </summary>
public sealed class ProxiedOutboundHttp : IOutboundHttp, IDisposable
{
    private readonly HttpClient _https;
    private readonly HttpClient _http;
    private readonly AuthenticationHeaderValue? _proxyAuth;

    public ProxiedOutboundHttp(Uri? proxy)
    {
        if (proxy is null)
        {
            // No proxy configured (tests, local runs): connect directly.
            _https = _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
            return;
        }

        var user = Uri.UnescapeDataString(proxy.UserInfo);
        _proxyAuth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user)));
        var endpoint = new DnsEndPoint(proxy.Host, proxy.Port);
        _https = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = (context, ct) => TunnelAsync(endpoint, context.DnsEndPoint, ct),
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = true,
            Proxy = new WebProxy(new UriBuilder(proxy) { UserName = "", Password = "" }.Uri),
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.Scheme == Uri.UriSchemeHttps)
            return _https.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (_proxyAuth is not null)
            request.Headers.ProxyAuthorization = _proxyAuth;
        return _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async ValueTask<Stream> TunnelAsync(DnsEndPoint proxy, DnsEndPoint target, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(proxy, ct);
            var stream = new NetworkStream(socket, ownsSocket: true);
            var head = $"CONNECT {target.Host}:{target.Port} HTTP/1.1\r\nHost: {target.Host}:{target.Port}\r\n" +
                       $"Proxy-Authorization: {_proxyAuth}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);

            // Read the proxy's answer byte by byte, so nothing after it is consumed.
            var answer = new StringBuilder();
            var one = new byte[1];
            while (!answer.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (answer.Length > 8192 || await stream.ReadAsync(one, ct) == 0)
                    throw new HttpRequestException("The egress proxy closed the connection.");
                answer.Append((char)one[0]);
            }
            var status = answer.ToString().Split(' ', 3);
            if (status.Length < 2 || status[1] != "200")
                throw new HttpRequestException($"The egress proxy refused the connection ({(status.Length > 1 ? status[1] : "?")}).");
            return stream;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _https.Dispose();
        if (!ReferenceEquals(_http, _https))
            _http.Dispose();
    }
}
