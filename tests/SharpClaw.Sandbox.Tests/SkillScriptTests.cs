using System.Runtime.CompilerServices;
using System.Text.Json;
using SharpClaw.Agent.Tools.Workspace;
using SharpClaw.Sandbox.Infrastructure;
using SharpClaw.Sandbox.Ops;

namespace SharpClaw.Sandbox.Tests;

/// <summary>
/// The shipped <c>csv-summary</c> skill's script run the way the model runs it: through
/// <c>sandbox_run</c> from the workspace root (skills spec section 8). The test points at the
/// repo's copy of the script; in a sandbox it is at <c>/skills/csv-summary/scripts/summarize.py</c>.
/// </summary>
public class SkillScriptTests : IDisposable
{
    private readonly TempWorkspace _ws = new();
    private readonly SandboxRunOp _op;

    public SkillScriptTests() => _op = new SandboxRunOp(new WorkspaceRoot(_ws.Path), ChildEnvironment.Build(), TimeProvider.System);

    public void Dispose() => _ws.Dispose();

    private static string Script([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..",
            "docker", "template", "skills", "csv-summary", "scripts", "summarize.py"));

    private async Task<JsonElement> Run(string command)
    {
        var json = (await _op.RunAsync(JsonSerializer.SerializeToElement(new { command }), default)).Json;
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public async Task Csv_summary_script_summarises_a_workspace_file()
    {
        Directory.CreateDirectory(_ws.File("data"));
        File.WriteAllText(_ws.File("data/sales.csv"),
            "region,units,price\nNorth,10,2.50\nSouth,5,\"1,200.00\"\nNorth,,3.00\n");

        var r = await Run($"python3 '{Script()}' data/sales.csv");

        Assert.True(r.GetProperty("exitCode").GetInt32() == 0, r.GetProperty("stderr").GetString());
        var stdout = r.GetProperty("stdout").GetString()!;
        Assert.Contains("# Summary of data/sales.csv", stdout);
        Assert.Contains("- Rows: 3", stdout);
        Assert.Contains("| region | text | 0 | 2 distinct; most common: North (2), South (1) |", stdout);
        Assert.Contains("| units | number | 1 | min 5, max 10, mean 7.50, sum 15 |", stdout);
        Assert.Contains("| price | number | 0 | min 2.50, max 1,200, mean 401.83, sum 1,205.50 |", stdout);

        // It only reads: nothing new appears in the workspace.
        Assert.Equal(new[] { _ws.File("data/sales.csv") }, Directory.GetFiles(_ws.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Csv_summary_script_reports_a_missing_file()
    {
        var r = await Run($"python3 '{Script()}' missing.csv");

        Assert.True(r.GetProperty("exitCode").GetInt32() == 1, r.GetProperty("stderr").GetString());
        Assert.Contains("no such file: missing.csv", r.GetProperty("stderr").GetString());
    }
}
