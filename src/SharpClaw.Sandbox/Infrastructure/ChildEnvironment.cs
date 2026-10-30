namespace SharpClaw.Sandbox.Infrastructure;

/// <summary>
/// The environment every child process gets (spec section 6): proxy variables (the sandbox's own
/// proxy credential), toolchain and cache settings, <c>HOME=/tmp/home</c>. Built from an allow-list,
/// so the runner's service secret and anything else in its environment never reach a child.
/// </summary>
public static class ChildEnvironment
{
    private static readonly string[] Names =
    [
        "PATH", "LANG", "LC_ALL", "TZ", "TERM",
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy",
    ];

    private static readonly string[] Prefixes = ["DOTNET_", "NUGET_", "npm_config_", "NPM_CONFIG_", "PIP_", "NODE_", "XDG_", "GIT_"];

    public static IReadOnlyDictionary<string, string> Build(System.Collections.IDictionary? source = null, string home = "/tmp/home")
    {
        source ??= Environment.GetEnvironmentVariables();
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in source)
        {
            var name = (string)entry.Key;
            if (entry.Value is not string value || name.Contains("SECRET", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Names.Contains(name) || Prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
                env[name] = value;
        }

        // Tools disagree on upper or lower case; give them both.
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY" })
        {
            if (env.TryGetValue(name, out var upper))
                env.TryAdd(name.ToLowerInvariant(), upper);
            else if (env.TryGetValue(name.ToLowerInvariant(), out var lower))
                env[name] = lower;
        }
        env["HOME"] = home;
        env["GIT_TERMINAL_PROMPT"] = "0";
        env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        env.TryAdd("PATH", "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin");
        return env;
    }
}
