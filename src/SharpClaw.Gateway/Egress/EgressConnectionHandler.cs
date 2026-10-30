using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway.Egress;

/// <summary>
/// The egress proxy (external tools spec section 5): a raw TCP handler on its own listener, so no
/// ASP.NET Core routing, middleware or endpoints run on it. It reads one bounded request head and
/// accepts exactly two forms: <c>CONNECT host:443</c> (the TLS tunnel is never parsed) and
/// absolute-form plain HTTP to port 80 (rewritten to origin form, one request per connection).
/// Before connecting it checks the sandbox's proxy credential, the port, the deny-list, and every
/// resolved address, then connects to the checked address without resolving again.
/// </summary>
public sealed class EgressConnectionHandler(
    IOptions<EgressOptions> options,
    IEgressResolver resolver,
    IEgressConnector connector,
    TimeProvider time,
    GatewayMetrics metrics,
    ILogger<EgressConnectionHandler> logger) : ConnectionHandler
{
    private const int CopyBufferBytes = 16 * 1024;
    private const string ProxyUserPrefix = "sbx-";

    // Hop-by-hop and proxy headers never forwarded upstream (plus any named in Connection).
    private static readonly HashSet<string> DroppedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authorization", "Proxy-Authenticate",
        "TE", "Trailer", "Upgrade",
    };

    private readonly ConcurrentDictionary<string, int> _open = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Statuses for the <c>sharpclaw.egress.connections</c> metric and the connection log.</summary>
    public static class Decisions
    {
        public const string Allowed = "allowed";
        public const string BadRequest = "bad_request";
        public const string AuthFailed = "auth_failed";
        public const string Blocked = "blocked";
        public const string Limited = "limited";
        public const string Timeout = "timeout";
        public const string UpstreamError = "upstream_error";
        public const string Closed = "closed";
    }

    /// <summary>What one connection did, for the audit log and metrics. Never paths, queries, headers or bodies.</summary>
    private sealed class Audit
    {
        public string Agent = "unknown";
        public string Type = "unknown";
        public string? Host;
        public int? Port;
        public string? Method;
        public string Decision = Decisions.Closed;
        public long BytesOut;
        public long BytesIn;
    }

    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        var sw = Stopwatch.StartNew();
        var audit = new Audit();
        try
        {
            await HandleAsync(connection, audit);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ConnectionResetException)
        {
            // The client or upstream went away; whatever was decided stands.
        }
        finally
        {
            metrics.RecordEgress(audit.Agent, audit.Type, audit.Decision, audit.BytesOut + audit.BytesIn);
            using (logger.BeginScope(new Dictionary<string, object> { [LogScopes.Agent] = audit.Agent }))
                logger.LogInformation(
                    "egress: type={Type} host={Host} port={Port} method={Method} decision={Decision} bytes_out={BytesOut} bytes_in={BytesIn} duration_ms={DurationMs}",
                    audit.Type, audit.Host, audit.Port, audit.Method, audit.Decision, audit.BytesOut, audit.BytesIn, sw.ElapsedMilliseconds);
        }
    }

    private async Task HandleAsync(ConnectionContext connection, Audit audit)
    {
        var opts = options.Value;
        var input = connection.Transport.Input;
        var output = connection.Transport.Output;

        // 1. One request head, bounded in size and time.
        byte[]? headBytes;
        using (var headTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(opts.HeadTimeoutSeconds), time))
        using (var headCts = CancellationTokenSource.CreateLinkedTokenSource(headTimeout.Token, connection.ConnectionClosed))
        {
            try
            {
                headBytes = await ReadHeadAsync(input, opts.MaxHeadBytes, headCts.Token);
            }
            catch (OperationCanceledException) when (headTimeout.IsCancellationRequested)
            {
                await RefuseAsync(output, audit, Decisions.Timeout, 408, "Request Timeout");
                return;
            }
        }
        if (headBytes is null)
            return; // closed before a full head arrived
        if (headBytes.Length == 0)
        {
            await RefuseAsync(output, audit, Decisions.BadRequest, 431, "Request Header Fields Too Large");
            return;
        }

        var head = ProxyRequestHead.TryParse(headBytes);
        if (head is null)
        {
            await RefuseAsync(output, audit, Decisions.BadRequest, 400, "Bad Request");
            return;
        }
        var isConnect = head.Method == "CONNECT";
        audit.Type = isConnect ? "connect" : "http";
        audit.Method = isConnect ? null : head.Method;

        // Exactly two forms: CONNECT host:port, or http://host[:port]/path. Anything else (an
        // origin-form request for a gateway route, https:// without CONNECT…) is refused first.
        var target = isConnect ? ParseConnectTarget(head.Target) : ParseHttpTarget(head.Target);
        if (target is null)
        {
            await RefuseAsync(output, audit, Decisions.BadRequest, 400, "Bad Request");
            return;
        }

        // 2. Who is this? Only a sandbox's own proxy credential identifies it; agents have none.
        var credentials = head.Values("Proxy-Authorization").ToList();
        var sandbox = credentials.Count == 1 ? Authenticate(credentials[0], opts) : null;
        if (sandbox is null)
        {
            metrics.RecordAuthFailure("egress", "proxy_credential");
            await RefuseAsync(output, audit, Decisions.AuthFailed, 407, "Proxy Authentication Required",
                "Proxy-Authenticate: Basic realm=\"sharpclaw-egress\"\r\n");
            return;
        }
        audit.Agent = sandbox;

        if (_open.AddOrUpdate(sandbox, 1, (_, n) => n + 1) > opts.MaxConnectionsPerSandbox)
        {
            _open.AddOrUpdate(sandbox, 0, (_, n) => n - 1);
            await RefuseAsync(output, audit, Decisions.Limited, 429, "Too Many Requests");
            return;
        }
        try
        {
            await ForwardAsync(connection, head, isConnect, target.Value, audit, opts);
        }
        finally
        {
            _open.AddOrUpdate(sandbox, 0, (_, n) => n - 1);
        }
    }

    private async Task ForwardAsync(
        ConnectionContext connection, ProxyRequestHead head, bool isConnect,
        (string Host, int Port, string? PathAndQuery) target, Audit audit, EgressOptions opts)
    {
        var output = connection.Transport.Output;

        // 3. Port 443 for CONNECT and 80 for plain HTTP only; the operator's deny-list.
        var (host, port, pathAndQuery) = target;
        audit.Host = host;
        audit.Port = port;
        if (port != (isConnect ? 443 : 80) || IsDenied(host, opts))
        {
            await RefuseAsync(output, audit, Decisions.Blocked, 403, "Forbidden");
            return;
        }

        // 4. Resolve once and refuse if ANY address is private or reserved; then connect to that address.
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed);
        connectCts.CancelAfter(TimeSpan.FromSeconds(opts.ConnectTimeoutSeconds));
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
                ? [literal]
                : await resolver.ResolveAsync(host, connectCts.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            await RefuseAsync(output, audit, Decisions.UpstreamError, 502, "Bad Gateway");
            return;
        }
        if (addresses.Length == 0 || addresses.Any(EgressAddressPolicy.IsBlocked))
        {
            await RefuseAsync(output, audit, addresses.Length == 0 ? Decisions.UpstreamError : Decisions.Blocked,
                addresses.Length == 0 ? 502 : 403, addresses.Length == 0 ? "Bad Gateway" : "Forbidden");
            return;
        }
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];

        Socket upstream;
        try
        {
            upstream = await connector.ConnectAsync(new IPEndPoint(address, port), connectCts.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            await RefuseAsync(output, audit, Decisions.UpstreamError, 502, "Bad Gateway");
            return;
        }

        using (upstream)
        {
            audit.Decision = Decisions.Allowed;
            if (isConnect)
                await output.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray());
            else
                await SendAllAsync(upstream, RewriteHead(head, host, pathAndQuery!), connection.ConnectionClosed);
            await PumpAsync(connection, upstream, audit, opts);
        }
    }

    /// <summary>
    /// Copies bytes both ways with fixed-size buffers, never parsing them. Stops on the per-connection
    /// byte cap, after the idle timeout with no traffic either way, or when either side closes.
    /// </summary>
    private async Task PumpAsync(ConnectionContext connection, Socket upstream, Audit audit, EgressOptions opts)
    {
        var idle = TimeSpan.FromSeconds(opts.IdleTimeoutSeconds);
        using var idleCts = new CancellationTokenSource(idle, time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(idleCts.Token, connection.ConnectionClosed);
        long total = 0;

        bool Count(int n, ref long direction)
        {
            direction += n;
            idleCts.CancelAfter(idle);
            if (Interlocked.Add(ref total, n) <= opts.MaxBytesPerConnection)
                return true;
            audit.Decision = Decisions.Limited;
            cts.Cancel();
            return false;
        }

        async Task UpAsync()
        {
            var input = connection.Transport.Input;
            while (true)
            {
                var result = await input.ReadAsync(cts.Token);
                foreach (var segment in result.Buffer)
                {
                    if (!Count(segment.Length, ref audit.BytesOut))
                        return;
                    await SendAllAsync(upstream, segment, cts.Token);
                }
                input.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                    return;
            }
        }

        async Task DownAsync()
        {
            var output = connection.Transport.Output;
            var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
            try
            {
                while (true)
                {
                    var n = await upstream.ReceiveAsync(buffer.AsMemory(0, CopyBufferBytes), SocketFlags.None, cts.Token);
                    if (n == 0 || !Count(n, ref audit.BytesIn))
                        return;
                    await output.WriteAsync(buffer.AsMemory(0, n), cts.Token);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        var up = UpAsync();
        var down = DownAsync();
        var first = await Task.WhenAny(up, down);
        if (first == up && up.IsCompletedSuccessfully && !cts.IsCancellationRequested)
        {
            // The client finished sending: tell upstream, and keep relaying its answer.
            try { upstream.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
            await Swallow(down);
        }
        cts.Cancel();
        await Swallow(up);
        await Swallow(down);
    }

    private static async Task Swallow(Task task)
    {
        try { await task; }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ConnectionResetException or InvalidOperationException) { }
    }

    private static async Task SendAllAsync(Socket socket, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        while (!data.IsEmpty)
        {
            var sent = await socket.SendAsync(data, SocketFlags.None, ct);
            data = data[sent..];
        }
    }

    /// <summary>Reads up to the blank line. Null: closed first. Empty: over the size limit.</summary>
    private static async Task<byte[]?> ReadHeadAsync(PipeReader input, int maxBytes, CancellationToken ct)
    {
        while (true)
        {
            var result = await input.ReadAsync(ct);
            var buffer = result.Buffer;
            var length = HeadLength(buffer);
            if (length > maxBytes || (length < 0 && buffer.Length > maxBytes))
            {
                input.AdvanceTo(buffer.Start, buffer.End);
                return [];
            }
            if (length > 0)
            {
                var head = buffer.Slice(0, length).ToArray();
                // Bytes after the head (a TLS ClientHello, a request body) stay in the pipe for the pump.
                input.AdvanceTo(buffer.GetPosition(length));
                return head;
            }
            if (result.IsCompleted)
            {
                input.AdvanceTo(buffer.End);
                return null;
            }
            input.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>Length of the head including CRLF CRLF, or -1 if it isn't complete yet.</summary>
    private static long HeadLength(ReadOnlySequence<byte> buffer)
    {
        var reader = new SequenceReader<byte>(buffer);
        return reader.TryReadTo(out ReadOnlySequence<byte> _, "\r\n\r\n"u8, advancePastDelimiter: true)
            ? reader.Consumed
            : -1;
    }

    /// <summary>The sandbox name for a valid <c>Basic sbx-&lt;name&gt;:&lt;secret&gt;</c> credential, else null.</summary>
    internal static string? Authenticate(string header, EgressOptions opts)
    {
        const string scheme = "Basic ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return null;
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[scheme.Length..].Trim()));
        }
        catch (FormatException)
        {
            return null;
        }
        var colon = decoded.IndexOf(':');
        if (colon < 0 || !decoded.StartsWith(ProxyUserPrefix, StringComparison.Ordinal))
            return null;
        var name = decoded[ProxyUserPrefix.Length..colon];
        var secret = decoded[(colon + 1)..];
        var match = opts.Configured.FirstOrDefault(s => string.Equals(s.Key, name, StringComparison.OrdinalIgnoreCase));
        return match.Value is { } sandbox && Hashing.FixedTimeEquals(secret, sandbox.Secret) ? match.Key : null;
    }

    /// <summary><c>host:port</c> (port required), with IPv6 literals in brackets.</summary>
    internal static (string Host, int Port, string? PathAndQuery)? ParseConnectTarget(string target) =>
        ParseAuthority(target, defaultPort: null) is { } a ? (a.Host, a.Port, null) : null;

    /// <summary><c>http://host[:port][/path][?query]</c>, no user info. The path is kept exactly as sent.</summary>
    internal static (string Host, int Port, string? PathAndQuery)? ParseHttpTarget(string target)
    {
        const string scheme = "http://";
        if (!target.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return null;
        var rest = target[scheme.Length..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var authority = end < 0 ? rest : rest[..end];
        var path = end < 0 ? "/" : rest[end..];
        if (path.StartsWith('#'))
            path = "/";
        else if (path.StartsWith('?'))
            path = "/" + path;
        var hash = path.IndexOf('#');
        if (hash >= 0)
            path = path[..hash];
        if (authority.Contains('@'))
            return null;
        return ParseAuthority(authority, defaultPort: 80) is { } a ? (a.Host, a.Port, path) : null;
    }

    private static (string Host, int Port)? ParseAuthority(string authority, int? defaultPort)
    {
        string host;
        string? portText = null;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0 || !IPAddress.TryParse(authority[1..close], out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6)
                return null;
            host = authority[..(close + 1)];
            var after = authority[(close + 1)..];
            if (after.Length > 0)
            {
                if (!after.StartsWith(':'))
                    return null;
                portText = after[1..];
            }
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
            portText = colon < 0 ? null : authority[(colon + 1)..];
            host = host.TrimEnd('.').ToLowerInvariant();
            if (!IsHostName(host))
                return null;
        }

        int port;
        if (portText is null)
        {
            if (defaultPort is null)
                return null;
            port = defaultPort.Value;
        }
        else if (portText.Length is 0 or > 5 || !portText.All(char.IsAsciiDigit) || !int.TryParse(portText, out port) || port is < 1 or > 65535)
        {
            return null;
        }
        return (host, port);
    }

    /// <summary>An ASCII DNS name (IDNs arrive punycoded) or an IPv4 literal.</summary>
    private static bool IsHostName(string host) =>
        host.Length is > 0 and <= 253 &&
        host.Split('.').All(label => label.Length is > 0 and <= 63 &&
            label.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-') &&
            !label.StartsWith('-') && !label.EndsWith('-'));

    private static bool IsDenied(string host, EgressOptions opts) =>
        opts.DenyHosts.Any(d =>
        {
            var deny = d.Trim().TrimEnd('.').ToLowerInvariant();
            return host == deny || host.EndsWith("." + deny, StringComparison.Ordinal);
        });

    /// <summary>
    /// Origin-form request line, <c>Host</c> from the checked target, proxy and hop-by-hop headers
    /// removed, and <c>Connection: close</c> so the connection carries one request.
    /// </summary>
    internal static byte[] RewriteHead(ProxyRequestHead head, string host, string pathAndQuery)
    {
        var connectionTokens = head.Values("Connection")
            .SelectMany(v => v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        var dropped = new HashSet<string>(DroppedHeaders.Concat(connectionTokens), StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.Append(head.Method).Append(' ').Append(pathAndQuery).Append(" HTTP/1.1\r\n");
        sb.Append("Host: ").Append(host).Append("\r\n");
        foreach (var (name, value) in head.Headers)
            if (!dropped.Contains(name))
                sb.Append(name).Append(": ").Append(value).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static async Task RefuseAsync(PipeWriter output, Audit audit, string decision, int status, string reason, string extraHeaders = "")
    {
        audit.Decision = decision;
        var response = $"HTTP/1.1 {status} {reason}\r\n{extraHeaders}Content-Length: 0\r\nConnection: close\r\n\r\n";
        await output.WriteAsync(Encoding.ASCII.GetBytes(response));
    }
}
