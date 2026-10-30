using System.Text;

namespace SharpClaw.Gateway.Egress;

/// <summary>
/// One proxy request head (request line and headers), parsed strictly: ASCII only, CRLF line ends,
/// a single-space request line, token header names and no folded lines. Anything else is refused
/// rather than interpreted, so the proxy and the upstream server can't read a request differently.
/// </summary>
public sealed class ProxyRequestHead
{
    private ProxyRequestHead(string method, string target, string version, List<KeyValuePair<string, string>> headers)
    {
        Method = method;
        Target = target;
        Version = version;
        Headers = headers;
    }

    public string Method { get; }
    public string Target { get; }
    public string Version { get; }
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

    public IEnumerable<string> Values(string name) =>
        Headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);

    /// <summary>Parses <paramref name="head"/>, which ends with the blank line (CRLF CRLF).</summary>
    public static ProxyRequestHead? TryParse(ReadOnlySpan<byte> head)
    {
        foreach (var b in head)
            if (b is not (>= 0x20 and <= 0x7E) and not (byte)'\t' and not (byte)'\r' and not (byte)'\n')
                return null;

        var text = Encoding.ASCII.GetString(head);
        if (!text.EndsWith("\r\n\r\n", StringComparison.Ordinal))
            return null;
        var lines = text[..^4].Split("\r\n");
        if (lines.Any(l => l.Contains('\r') || l.Contains('\n')))
            return null;

        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !IsToken(parts[0]) || parts[1].Length == 0 || parts[2] is not ("HTTP/1.1" or "HTTP/1.0"))
            return null;

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            // No obs-fold (a line starting with whitespace) and no whitespace before the colon.
            if (colon <= 0 || !IsToken(line[..colon]))
                return null;
            headers.Add(new(line[..colon], line[(colon + 1)..].Trim(' ', '\t')));
        }
        return new ProxyRequestHead(parts[0], parts[1], parts[2], headers);
    }

    private static bool IsToken(string s) =>
        s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c));
}
