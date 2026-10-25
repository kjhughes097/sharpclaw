using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace SharpClaw.Agent.Tests;

/// <summary>
/// Architecture rule (3.2 spec section 7): tools may only be executed by ToolDispatcher, so no tool
/// call can skip the risk and approval checks. This scans the agent's source for call sites.
/// </summary>
public class ToolArchitectureTests
{
    private static string AgentSourceDir([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "src", "SharpClaw.Agent"));

    [Fact]
    public void Only_ToolDispatcher_calls_ExecuteUncheckedAsync()
    {
        var dir = AgentSourceDir();
        Assert.True(Directory.Exists(dir), $"agent source not found at {dir}");

        // A call site is ".ExecuteUncheckedAsync(" (declarations and doc comments don't have the dot).
        var callSite = new Regex(@"\.ExecuteUncheckedAsync\s*\(");
        var offenders = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Path.GetFileName(f) != "ToolDispatcher.cs")
            .Where(f => callSite.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, $"ExecuteUncheckedAsync called outside ToolDispatcher: {string.Join(", ", offenders)}");
        Assert.Matches(callSite, File.ReadAllText(Path.Combine(dir, "Tools", "ToolDispatcher.cs")));
    }
}
