using System.Globalization;
using System.Text;

namespace SharpClaw.Agent.Skills;

/// <summary>
/// A hand-written reader for the YAML frontmatter of a <c>SKILL.md</c> (skills spec section 3.2).
/// It supports only what <c>name</c> and <c>description</c> need: <c>key: value</c> lines, single
/// or double quoted values, and the folded (<c>&gt;</c>) or literal (<c>|</c>) block forms. No YAML
/// library, so there are no anchors, tags, aliases or nested structures to worry about. Every other
/// top-level field is skipped without being interpreted.
/// </summary>
/// <remarks>Error reasons never quote the file's content: they end up in startup logs.</remarks>
public static class SkillFrontmatter
{
    /// <summary>The frontmatter block, delimiters included, may be at most this many UTF-8 bytes.</summary>
    public const int MaxBytes = 4 * 1024;

    private const string Delimiter = "---";

    /// <summary>The two fields the loader reads. Null when absent.</summary>
    public sealed record Fields(string? Name, string? Description);

    /// <summary>Parses the frontmatter at the start of <paramref name="text"/>, or returns an error reason.</summary>
    public static (Fields? Fields, string? Error) Parse(string text)
    {
        if (text.StartsWith('\uFEFF'))
            text = text[1..];

        var lines = text.Split('\n');
        if (lines[0].TrimEnd('\r', ' ', '\t') != Delimiter)
            return (null, "SKILL.md must start with a --- frontmatter line");

        // Find the closing delimiter, counting bytes as we go so a huge block is refused early.
        var bytes = Encoding.UTF8.GetByteCount(lines[0]) + 1;
        var close = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            bytes += Encoding.UTF8.GetByteCount(lines[i]) + 1;
            if (bytes > MaxBytes)
                return (null, $"frontmatter is larger than {MaxBytes / 1024} KB or is not closed");
            if (lines[i].TrimEnd('\r', ' ', '\t') == Delimiter)
            {
                close = i;
                break;
            }
        }
        if (close < 0)
            return (null, "frontmatter is not closed with a --- line");

        return ParseFields(lines[1..close].Select(l => l.TrimEnd('\r')).ToArray());
    }

    private static (Fields?, string?) ParseFields(string[] lines)
    {
        string? name = null, description = null;
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (IsBlankOrComment(line))
            {
                i++;
                continue;
            }
            if (char.IsWhiteSpace(line[0]) || line.StartsWith('-'))
                return (null, "frontmatter has an unexpected indented or list line");

            var colon = line.IndexOf(':');
            var key = colon > 0 ? line[..colon] : null;
            if (key is null || !IsPlainKey(key) || (colon + 1 < line.Length && line[colon + 1] is not (' ' or '\t')))
                return (null, "frontmatter has a line that is not 'key: value'");

            // Everything indented (or a '- item') below a key belongs to that key.
            var end = i + 1;
            while (end < lines.Length && (IsBlankOrComment(lines[end]) || char.IsWhiteSpace(lines[end][0]) || lines[end].StartsWith('-')))
                end++;

            if (key is "name" or "description")
            {
                if ((key == "name" ? name : description) is not null)
                    return (null, $"{key} appears more than once");
                var (value, error) = ReadValue(key, line[(colon + 1)..].Trim(), lines[(i + 1)..end], allowBlock: key == "description");
                if (error is not null)
                    return (null, error);
                if (key == "name")
                    name = value;
                else
                    description = value;
            }
            // Any other field (license, allowed-tools, metadata, ...) is ignored, never interpreted.
            i = end;
        }
        return (new Fields(name, description), null);
    }

    private static (string? Value, string? Error) ReadValue(string key, string raw, string[] continuation, bool allowBlock)
    {
        if (raw.Length > 0 && raw[0] is '|' or '>')
        {
            if (!allowBlock)
                return (null, $"{key} must be a single-line value");
            var indicator = StripComment(raw);
            if (indicator is not ("|" or ">" or "|-" or ">-" or "|+" or ">+"))
                return (null, $"{key} uses an unsupported block indicator");
            return ReadBlock(key, continuation, folded: indicator[0] == '>');
        }

        if (continuation.Any(l => !IsBlankOrComment(l)))
            return (null, $"{key} continues over several lines; quote it on one line or use > or |");

        if (raw.Length == 0)
            return ("", null);
        if (raw[0] == '\'')
            return ReadSingleQuoted(key, raw);
        if (raw[0] == '"')
            return ReadDoubleQuoted(key, raw);

        // A plain scalar. Characters that start YAML structure (flow collections, anchors, aliases,
        // tags, directives) are refused rather than guessed at.
        if (raw[0] is '[' or '{' or '&' or '*' or '!' or '%' or '@' or '`' or ',' or '?' or '#')
            return (null, $"{key} uses an unsupported YAML construct");
        var value = StripComment(raw);
        if (value.Contains(": ") || value.EndsWith(':'))
            return (null, $"{key} contains ': '; put the value in quotes");
        return (value, null);
    }

    /// <summary>
    /// A block scalar: the lines indented below the key. Folded joins lines with spaces (a blank line
    /// is a line break); literal keeps the breaks. The loader folds every break to one line anyway.
    /// </summary>
    private static (string?, string?) ReadBlock(string key, string[] lines, bool folded)
    {
        var sb = new StringBuilder();
        var indent = -1;
        var pendingBreak = false;
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                if (indent >= 0)
                {
                    sb.Append('\n');
                    pendingBreak = !folded && pendingBreak;
                }
                continue;
            }

            var lineIndent = line.Length - line.TrimStart().Length;
            if (indent < 0 && lineIndent > 0)
                indent = lineIndent;
            if (lineIndent < indent || indent < 0)
            {
                // Only a comment may sit less indented than the block's text.
                if (line.TrimStart().StartsWith('#'))
                    continue;
                return (null, $"{key} block is not indented consistently");
            }

            if (pendingBreak)
                sb.Append(folded ? ' ' : '\n');
            sb.Append(line[indent..]);
            pendingBreak = true;
        }
        return (sb.ToString(), null);
    }

    private static (string?, string?) ReadSingleQuoted(string key, string raw)
    {
        var sb = new StringBuilder();
        for (var i = 1; i < raw.Length; i++)
        {
            if (raw[i] != '\'')
            {
                sb.Append(raw[i]);
                continue;
            }
            if (i + 1 < raw.Length && raw[i + 1] == '\'')
            {
                sb.Append('\'');
                i++;
                continue;
            }
            return OnlyCommentAfter(raw[(i + 1)..]) ? (sb.ToString(), null) : (null, $"{key} has text after its closing quote");
        }
        return (null, $"{key} has an unclosed quote");
    }

    private static (string?, string?) ReadDoubleQuoted(string key, string raw)
    {
        var sb = new StringBuilder();
        for (var i = 1; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '"')
                return OnlyCommentAfter(raw[(i + 1)..]) ? (sb.ToString(), null) : (null, $"{key} has text after its closing quote");
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            if (++i >= raw.Length)
                break;
            switch (raw[i])
            {
                case '\\': sb.Append('\\'); break;
                case '"': sb.Append('"'); break;
                case '/': sb.Append('/'); break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'u' when i + 4 < raw.Length &&
                    ushort.TryParse(raw.AsSpan(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code):
                    sb.Append((char)code);
                    i += 4;
                    break;
                default:
                    return (null, $"{key} uses an unsupported escape sequence");
            }
        }
        return (null, $"{key} has an unclosed quote");
    }

    private static bool OnlyCommentAfter(string rest)
    {
        var trimmed = rest.TrimStart();
        return trimmed.Length == 0 || (trimmed[0] == '#' && rest.Length > trimmed.Length);
    }

    /// <summary>Removes a trailing YAML comment (" #...") from a plain value.</summary>
    private static string StripComment(string value)
    {
        var hash = value.IndexOf(" #", StringComparison.Ordinal);
        var tab = value.IndexOf("\t#", StringComparison.Ordinal);
        var cut = hash < 0 ? tab : tab < 0 ? hash : Math.Min(hash, tab);
        return (cut < 0 ? value : value[..cut]).Trim();
    }

    private static bool IsBlankOrComment(string line) =>
        line.Trim().Length == 0 || line.TrimStart().StartsWith('#');

    private static bool IsPlainKey(string key) =>
        key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
