using System.Net.Sockets;
using static SharpClaw.Gateway.Tests.EgressHarness;

namespace SharpClaw.Gateway.Tests;

/// <summary>Egress limits (Q7): head size and time, connections per sandbox, bytes per connection.</summary>
public partial class EgressProxyTests
{
    [Fact]
    public async Task An_oversized_head_gets_431()
    {
        await using var h = await StartAsync(new() { ["Egress:MaxHeadBytes"] = "1024" });

        var response = await h.SendAsync(Connect("public.test:443", ProxyAuth + $"X-Pad: {new string('a', 2000)}\r\n"));

        Assert.StartsWith("HTTP/1.1 431 ", response);
        Assert.Empty(h.Connector.Calls);
    }

    [Fact]
    public async Task An_endless_head_without_a_blank_line_gets_431()
    {
        await using var h = await StartAsync(new() { ["Egress:MaxHeadBytes"] = "1024" });
        using var client = await h.ConnectAsync();

        await client.WriteAsync("CONNECT public.test:443 HTTP/1.1\r\n" + new string('a', 3000));

        Assert.StartsWith("HTTP/1.1 431 ", await client.ReadHeadAsync());
    }

    [Fact]
    public async Task A_slow_head_times_out_with_408()
    {
        await using var h = await StartAsync();
        using var client = await h.ConnectAsync();
        await client.WriteAsync("CONNECT public.test:443 HTTP/1.1\r\n");

        // The handler's head timer may start a moment after the connect: keep advancing until it fires.
        await TestHelpers.EventuallyAsync(() =>
        {
            h.Fx.Time.Advance(TimeSpan.FromSeconds(31));
            return client.Socket.Poll(0, SelectMode.SelectRead);
        });

        Assert.StartsWith("HTTP/1.1 408 ", await client.ReadHeadAsync());
        Assert.Empty(h.Connector.Calls);
    }

    [Fact]
    public async Task Connections_per_sandbox_are_limited()
    {
        await using var h = await StartAsync(new() { ["Egress:MaxConnectionsPerSandbox"] = "2" });
        h.Server.Echo = true;
        using var first = await h.ConnectAsync();
        using var second = await h.ConnectAsync();
        await first.WriteAsync(Connect("public.test:443"));
        await second.WriteAsync(Connect("public.test:443"));
        Assert.StartsWith("HTTP/1.1 200", await first.ReadHeadAsync());
        Assert.StartsWith("HTTP/1.1 200", await second.ReadHeadAsync());

        Assert.StartsWith("HTTP/1.1 429 ", await h.SendAsync(Connect("public.test:443")));

        // Closing one frees its slot.
        first.Dispose();
        await TestHelpers.EventuallyAsync(async () =>
        {
            using var again = await h.ConnectAsync();
            await again.WriteAsync(Connect("public.test:443"));
            return (await again.ReadHeadAsync()).StartsWith("HTTP/1.1 200");
        });
    }

    [Fact]
    public async Task A_connection_over_the_byte_cap_is_closed()
    {
        await using var h = await StartAsync(new() { ["Egress:MaxBytesPerConnection"] = "1024" });
        h.Server.Echo = true;
        using var client = await h.ConnectAsync();
        await client.WriteAsync(Connect("public.test:443"));
        Assert.StartsWith("HTTP/1.1 200", await client.ReadHeadAsync());

        await client.WriteAsync(new string('x', 400));
        Assert.Equal(new string('x', 400), await client.ReadExactlyAsync(400)); // 800 bytes counted, both ways
        await client.WriteAsync(new string('y', 400));                           // 1200: over the cap

        Assert.Equal("", await client.ReadToEndAsync()); // closed: the next chunk is over the cap
        await TestHelpers.EventuallyAsync(() => !h.Server.Received.IsEmpty);
        Assert.DoesNotContain("y", Assert.Single(h.Server.Received));
    }
}
