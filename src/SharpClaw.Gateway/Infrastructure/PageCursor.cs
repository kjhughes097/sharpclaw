using System.Globalization;
using System.Text;

namespace SharpClaw.Gateway.Infrastructure;

/// <summary>
/// An opaque keyset cursor: a UTC timestamp plus a row id as the tiebreaker. Clients must treat it
/// as opaque; it is base64url so it survives query strings unescaped.
/// </summary>
public readonly record struct PageCursor(DateTime At, Guid Id)
{
    public string Encode()
    {
        var raw = $"{At.Ticks.ToString(CultureInfo.InvariantCulture)}:{Id:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? value, out PageCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100)
            return false;
        try
        {
            var b64 = value.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split(':');
            if (parts.Length != 2 ||
                !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks ||
                !Guid.TryParseExact(parts[1], "N", out var id))
                return false;
            cursor = new PageCursor(new DateTime(ticks, DateTimeKind.Utc), id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
