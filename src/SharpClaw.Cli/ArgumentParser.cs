namespace SharpClaw.Cli;

/// <summary>Minimal argument parser: positionals plus <c>--key value</c> / <c>--flag</c> options.</summary>
public sealed class ParsedArgs
{
    public IReadOnlyList<string> Positionals { get; }
    private readonly Dictionary<string, string?> _options;

    private ParsedArgs(List<string> positionals, Dictionary<string, string?> options)
    {
        Positionals = positionals;
        _options = options;
    }

    public static ParsedArgs Parse(string[] args)
    {
        var positionals = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var key = token[2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    options[key] = args[++i];
                }
                else
                {
                    options[key] = null; // boolean flag
                }
            }
            else
            {
                positionals.Add(token);
            }
        }

        return new ParsedArgs(positionals, options);
    }

    public string? Get(string key) => _options.TryGetValue(key, out var v) ? v : null;
    public bool Has(string key) => _options.ContainsKey(key);

    public bool? GetBool(string key)
    {
        if (!_options.TryGetValue(key, out var v)) return null;
        if (v is null) return true;
        return bool.TryParse(v, out var b) ? b : null;
    }

    public double? GetDouble(string key) =>
        _options.TryGetValue(key, out var v) && double.TryParse(v, out var d) ? d : null;

    public int? GetInt(string key) =>
        _options.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : null;
}
