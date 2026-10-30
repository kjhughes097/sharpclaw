using System.Net;
using SharpClaw.Gateway.Egress;

namespace SharpClaw.Gateway.Tests;

/// <summary>Every blocked range (spec section 5), including IPv4 hidden inside IPv6 forms.</summary>
public class EgressAddressPolicyTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.255")]
    [InlineData("169.254.169.254")]       // cloud metadata
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]
    [InlineData("febf::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:127.0.0.1")]       // IPv4-mapped
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::127.0.0.1")]            // IPv4-compatible (deprecated)
    [InlineData("64:ff9b::192.168.1.1")]   // NAT64
    [InlineData("2002:c0a8:0101::1")]      // 6to4 wrapping 192.168.1.1
    public void Blocked(string address) => Assert.True(EgressAddressPolicy.IsBlocked(IPAddress.Parse(address)));

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("1.1.1.1")]
    [InlineData("100.63.255.255")]  // just below CGNAT
    [InlineData("100.128.0.0")]     // just above CGNAT
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.0")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:1.1.1.1")]
    [InlineData("64:ff9b::1.1.1.1")]
    [InlineData("2002:0101:0101::1")] // 6to4 wrapping 1.1.1.1
    public void Allowed(string address) => Assert.False(EgressAddressPolicy.IsBlocked(IPAddress.Parse(address)));
}
