using System.Net;
using System.Net.Sockets;
using static SharpClaw.Gateway.Tests.EgressHarness;

namespace SharpClaw.Gateway.Tests;

/// <summary>
/// The egress proxy listener end to end (external tools spec sections 5 and 10): proxy
/// authentication, the two accepted request forms, target checks, the plain-HTTP rewrite, and that
/// nothing else on the gateway is reachable through it. Limits are in <c>EgressProxyTests.Limits.cs</c>.
/// </summary>
public partial class EgressProxyTests
{
    private static string Connect(string target, string? auth = null) =>
        $"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n{auth ?? ProxyAuth}\r\n";

    [Fact]
    public async Task Connect_tunnels_bytes_to_the_checked_address_without_parsing_them()
    {
        await using var h = await StartAsync();
        h.Server.Echo = true;
        using var client = await h.ConnectAsync();

        await client.WriteAsync(Connect("public.test:443"));
        Assert.StartsWith("HTTP/1.1 200 Connection Established\r\n", await client.ReadHeadAsync());
        const string clientHello = "\u0016\u0003\u0001 not really TLS";
        await client.WriteAsync(clientHello);

        Assert.Equal(clientHello, await client.ReadExactlyAsync(clientHello.Length));
        Assert.Equal(new IPEndPoint(PublicIp, 443), Assert.Single(h.Connector.Calls));
        Assert.Equal(new[] { "public.test" }, h.Resolver.Calls);
    }

    [Fact]
    public async Task Bytes_sent_right_after_the_connect_head_are_forwarded()
    {
        await using var h = await StartAsync();
        h.Server.Echo = true;
        using var client = await h.ConnectAsync();

        await client.WriteAsync(Connect("public.test:443") + "HELLO");

        Assert.StartsWith("HTTP/1.1 200", await client.ReadHeadAsync());
        Assert.Equal("HELLO", await client.ReadExactlyAsync(5));
    }

    [Fact]
    public async Task Plain_http_is_rewritten_to_origin_form_with_proxy_and_hop_by_hop_headers_removed()
    {
        await using var h = await StartAsync();
        using var client = await h.ConnectAsync();

        await client.WriteAsync("GET http://public.test/a/b?q=1 HTTP/1.1\r\nHost: evil.test\r\n" + ProxyAuth +
                                "Proxy-Connection: keep-alive\r\nConnection: keep-alive, X-Drop\r\nX-Drop: 1\r\n" +
                                "Keep-Alive: timeout=5\r\nUpgrade: websocket\r\nUser-Agent: t\r\n\r\n");
        var response = await client.ReadToEndAsync();

        Assert.EndsWith("hello", response);
        await TestHelpers.EventuallyAsync(() => !h.Server.Received.IsEmpty);
        Assert.Equal("GET /a/b?q=1 HTTP/1.1\r\nHost: public.test\r\nUser-Agent: t\r\nConnection: close\r\n\r\n",
            Assert.Single(h.Server.Received));
        Assert.Equal(new IPEndPoint(PublicIp, 80), Assert.Single(h.Connector.Calls));
    }

    [Fact]
    public async Task A_second_request_on_one_connection_never_reaches_a_different_host()
    {
        await using var h = await StartAsync();
        using var client = await h.ConnectAsync();

        await client.WriteAsync("GET http://public.test/1 HTTP/1.1\r\n" + ProxyAuth + "\r\n" +
                                "GET http://other.test/2 HTTP/1.1\r\n" + ProxyAuth + "\r\n");
        await client.ReadToEndAsync();

        Assert.Equal(new[] { "public.test" }, h.Resolver.Calls);
        Assert.Equal(new IPEndPoint(PublicIp, 80), Assert.Single(h.Connector.Calls));
    }

    [Theory]
    [InlineData(null)]                                   // no credential
    [InlineData("Proxy-Authorization: Basic c2J4LWNvZHk6d3Jvbmc=\r\n")] // sbx-cody:wrong
    [InlineData("Proxy-Authorization: Bearer sc_agent_token_value\r\n")]
    [InlineData("Proxy-Authorization: Basic not-base64!\r\n")]
    public async Task Missing_or_wrong_credentials_get_407(string? auth)
    {
        await using var h = await StartAsync();

        var response = await h.SendAsync(Connect("public.test:443", auth ?? ""));

        Assert.StartsWith("HTTP/1.1 407 ", response);
        Assert.Contains("Proxy-Authenticate: Basic", response);
        Assert.Empty(h.Connector.Calls);
        Assert.Empty(h.Resolver.Calls);
    }

    [Fact]
    public async Task An_agent_token_is_not_a_proxy_credential()
    {
        await using var h = await StartAsync();
        var (_, token) = await h.Fx.CreateAgentAsync(Sandbox);

        foreach (var auth in new[] { Basic(Sandbox, token), Basic("sbx-" + Sandbox, token), "Bearer " + token })
            Assert.StartsWith("HTTP/1.1 407 ", await h.SendAsync(Connect("public.test:443", $"Proxy-Authorization: {auth}\r\n")));
        Assert.Empty(h.Connector.Calls);
    }

    [Fact]
    public async Task Two_credential_headers_are_refused()
    {
        await using var h = await StartAsync();

        Assert.StartsWith("HTTP/1.1 407 ", await h.SendAsync(Connect("public.test:443", ProxyAuth + ProxyAuth)));
    }

    [Theory]
    [InlineData("10.0.0.1:443")]
    [InlineData("127.0.0.1:443")]
    [InlineData("169.254.169.254:443")]
    [InlineData("100.64.1.1:443")]
    [InlineData("[::1]:443")]
    [InlineData("[fd00::1]:443")]
    [InlineData("[fe80::1]:443")]
    [InlineData("[::ffff:192.168.1.1]:443")]
    [InlineData("private.test:443")]
    [InlineData("mixed.test:443")]      // one public and one private address: refused
    [InlineData("localhost:443")]
    public async Task Private_and_reserved_targets_are_refused_before_connecting(string target)
    {
        await using var h = await StartAsync();

        Assert.StartsWith("HTTP/1.1 403 ", await h.SendAsync(Connect(target)));
        Assert.Empty(h.Connector.Calls);
    }

    [Fact]
    public async Task Plain_http_to_a_private_literal_is_refused()
    {
        await using var h = await StartAsync();

        Assert.StartsWith("HTTP/1.1 403 ", await h.SendAsync("GET http://192.168.1.1/admin HTTP/1.1\r\n" + ProxyAuth + "\r\n"));
        Assert.Empty(h.Connector.Calls);
    }

    [Theory]
    [InlineData("CONNECT public.test:22 HTTP/1.1\r\n")]
    [InlineData("CONNECT public.test:80 HTTP/1.1\r\n")]
    [InlineData("GET http://public.test:8080/ HTTP/1.1\r\n")]
    [InlineData("GET http://public.test:443/ HTTP/1.1\r\n")]
    public async Task Only_port_443_for_connect_and_80_for_http(string requestLine)
    {
        await using var h = await StartAsync();

        Assert.StartsWith("HTTP/1.1 403 ", await h.SendAsync(requestLine + ProxyAuth + "\r\n"));
        Assert.Empty(h.Connector.Calls);
    }

    [Fact]
    public async Task The_operator_deny_list_blocks_a_host_and_its_subdomains()
    {
        await using var h = await StartAsync(new() { ["Egress:DenyHosts:0"] = "test" });

        Assert.StartsWith("HTTP/1.1 403 ", await h.SendAsync(Connect("public.test:443")));
        Assert.Empty(h.Resolver.Calls);
    }

    [Theory]
    [InlineData("GARBAGE\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: gateway\r\n\r\n")]                 // origin form: not a proxy request
    [InlineData("GET https://public.test/ HTTP/1.1\r\n\r\n")]               // https must use CONNECT
    [InlineData("GET ftp://public.test/ HTTP/1.1\r\n\r\n")]
    [InlineData("GET http://user:pw@public.test/ HTTP/1.1\r\n\r\n")]        // credentials in the URL
    [InlineData("CONNECT public.test HTTP/1.1\r\n\r\n")]                    // no port
    [InlineData("CONNECT public.test:443  HTTP/1.1\r\n\r\n")]               // double space
    [InlineData("CONNECT public.test:443 HTTP/2\r\n\r\n")]
    [InlineData("CONNECT public.test:443 HTTP/1.1\r\nNoColon\r\n\r\n")]
    [InlineData("CONNECT public.test:443 HTTP/1.1\r\nX-A: 1\r\n folded\r\n\r\n")]
    [InlineData("CONNECT public.test:443 HTTP/1.1\r\nX-A : 1\r\n\r\n")]
    [InlineData("CONNECT public.test:443 HTTP/1.1\r\nX-A: caf\u00e9\r\n\r\n")]
    [InlineData("CONNECT pub\u00e9lic.test:443 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT -bad-.test:443 HTTP/1.1\r\n\r\n")]
    public async Task Malformed_requests_get_a_fixed_400(string head)
    {
        await using var h = await StartAsync();

        // Credentials come after the request line, so a bad line is refused whoever sends it.
        var response = await h.SendAsync(head.Replace("HTTP/1.1\r\n", "HTTP/1.1\r\n" + ProxyAuth));

        Assert.StartsWith("HTTP/1.1 400 ", response);
        Assert.Empty(h.Connector.Calls);
    }

    [Theory]
    [InlineData("GET /api/agents HTTP/1.1\r\nHost: gateway\r\nAuthorization: Bearer test-admin-key\r\n\r\n")]
    [InlineData("GET /healthz HTTP/1.1\r\nHost: gateway\r\n\r\n")]
    [InlineData("GET /agent/inbox HTTP/1.1\r\nHost: gateway\r\n\r\n")]
    public async Task No_client_or_agent_routes_are_reachable_on_the_egress_port(string head)
    {
        await using var h = await StartAsync();

        var response = await h.SendAsync(head);

        Assert.StartsWith("HTTP/1.1 400 ", response);
        Assert.DoesNotContain("\"status\"", response);
    }

    [Fact]
    public async Task The_listener_is_not_bound_without_sandbox_credentials()
    {
        var port = GatewayFixture.FreePort();
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: new Dictionary<string, string?> { ["Egress:Port"] = port.ToString() });

        using var tcp = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => tcp.ConnectAsync(IPAddress.Loopback, port));
    }

    [Fact]
    public async Task Logs_record_the_host_but_never_the_path_query_or_headers()
    {
        await using var h = await StartAsync();
        using var client = await h.ConnectAsync();

        await client.WriteAsync("GET http://public.test/SECRET_PATH?SECRET_QUERY=1 HTTP/1.1\r\n" + ProxyAuth +
                                "Cookie: SECRET_COOKIE\r\n\r\n");
        await client.ReadToEndAsync();

        await TestHelpers.EventuallyAsync(() => h.Logs.Any(l => l.Contains("egress:")));
        var logs = string.Join("\n", h.Logs);
        Assert.Contains("public.test", logs);
        foreach (var secret in new[] { "SECRET_PATH", "SECRET_QUERY", "SECRET_COOKIE", Secret })
            Assert.DoesNotContain(secret, logs);
    }
}
