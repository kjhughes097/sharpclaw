using System.Net;
using System.Net.Sockets;

namespace SharpClaw.Gateway.Egress;

/// <summary>
/// Which destination addresses the egress proxy refuses (spec section 5): loopback, private
/// (RFC 1918), link-local (including cloud metadata at 169.254.169.254), CGNAT, multicast,
/// unspecified, reserved and documentation ranges, IPv6 ULA and link-local, and IPv4 addresses
/// embedded in IPv6 (mapped, compatible, NAT64, 6to4) that fall in any of those. That also covers
/// the gateway itself, other containers and the LAN.
/// </summary>
public static class EgressAddressPolicy
{
    private static readonly (IPAddress Network, int Prefix)[] BlockedV4 =
    [
        (IPAddress.Parse("0.0.0.0"), 8),        // "this network", unspecified
        (IPAddress.Parse("10.0.0.0"), 8),       // private
        (IPAddress.Parse("100.64.0.0"), 10),    // CGNAT
        (IPAddress.Parse("127.0.0.0"), 8),      // loopback
        (IPAddress.Parse("169.254.0.0"), 16),   // link-local, cloud metadata
        (IPAddress.Parse("172.16.0.0"), 12),    // private (Docker's default range)
        (IPAddress.Parse("192.0.0.0"), 24),     // IETF protocol assignments
        (IPAddress.Parse("192.0.2.0"), 24),     // documentation
        (IPAddress.Parse("192.88.99.0"), 24),   // 6to4 relay anycast
        (IPAddress.Parse("192.168.0.0"), 16),   // private
        (IPAddress.Parse("198.18.0.0"), 15),    // benchmarking
        (IPAddress.Parse("198.51.100.0"), 24),  // documentation
        (IPAddress.Parse("203.0.113.0"), 24),   // documentation
        (IPAddress.Parse("224.0.0.0"), 4),      // multicast
        (IPAddress.Parse("240.0.0.0"), 4),      // reserved, broadcast
    ];

    private static readonly (IPAddress Network, int Prefix)[] BlockedV6 =
    [
        (IPAddress.Parse("::"), 128),           // unspecified
        (IPAddress.Parse("::1"), 128),          // loopback
        (IPAddress.Parse("100::"), 64),         // discard
        (IPAddress.Parse("2001:db8::"), 32),    // documentation
        (IPAddress.Parse("fc00::"), 7),         // unique local (ULA)
        (IPAddress.Parse("fe80::"), 10),        // link-local
        (IPAddress.Parse("fec0::"), 10),        // site-local (deprecated)
        (IPAddress.Parse("ff00::"), 8),         // multicast
    ];

    public static bool IsBlocked(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return BlockedV4.Any(r => InRange(address, r.Network, r.Prefix));
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return true;

        if (address.IsIPv4MappedToIPv6)
            return IsBlocked(address.MapToIPv4());
        var bytes = address.GetAddressBytes();
        // IPv4-compatible (::a.b.c.d, deprecated) and NAT64 (64:ff9b::/96) carry an IPv4 address
        // in the last 4 bytes; 6to4 (2002::/16) carries it in bytes 2-5.
        if (bytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0 || InRange(address, Nat64, 96))
            return IsBlocked(new IPAddress(bytes.AsSpan(12, 4)));
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
            return IsBlocked(new IPAddress(bytes.AsSpan(2, 4)));
        return BlockedV6.Any(r => InRange(address, r.Network, r.Prefix));
    }

    private static readonly IPAddress Nat64 = IPAddress.Parse("64:ff9b::");

    private static bool InRange(IPAddress address, IPAddress network, int prefix)
    {
        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();
        if (a.Length != n.Length)
            return false;
        var fullBytes = prefix / 8;
        if (!a.AsSpan(0, fullBytes).SequenceEqual(n.AsSpan(0, fullBytes)))
            return false;
        var rest = prefix % 8;
        if (rest == 0)
            return true;
        var mask = (byte)(0xFF << (8 - rest));
        return (a[fullBytes] & mask) == (n[fullBytes] & mask);
    }
}
