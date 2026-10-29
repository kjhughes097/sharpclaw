using SharpClaw.Agent.Tools;
using SharpClaw.Agent.Tools.Workspace;

namespace SharpClaw.Agent.Tests;

/// <summary>workspace_move and workspace_copy (agent tools spec section 4.5).</summary>
public partial class WorkspaceToolsTests
{
    [Fact]
    public async Task Move_renames_into_new_folders()
    {
        Put("a.md", "hello");

        var result = await Run("workspace_move", new { from = "a.md", to = "archive/2026/a.md" });

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal("a.md", result.GetProperty("from").GetString());
        Assert.Equal("archive/2026/a.md", result.GetProperty("to").GetString());
        Assert.False(result.GetProperty("overwritten").GetBoolean());
        Assert.False(File.Exists(Path.Combine(_root, "a.md")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_root, "archive", "2026", "a.md")));
    }

    [Fact]
    public async Task Copy_duplicates_the_file_atomically()
    {
        Put("a.md", "hello");

        var result = await Run("workspace_copy", new { from = "a.md", to = "copies/b.md" });

        Assert.Equal(5, result.GetProperty("bytes").GetInt64());
        Assert.False(result.GetProperty("overwritten").GetBoolean());
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_root, "a.md")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_root, "copies", "b.md")));
        Assert.Equal(new[] { "b.md" }, FilesIn("copies"));
    }

    [Theory]
    [InlineData("workspace_move")]
    [InlineData("workspace_copy")]
    public async Task Existing_destination_needs_overwrite(string tool)
    {
        Put("a.md", "new");
        var target = Put("b.md", "old");

        var refused = await Run(tool, new { from = "a.md", to = "b.md" });
        Assert.Equal("exists", refused.GetProperty("error").GetString());
        Assert.Equal("old", File.ReadAllText(target));

        var replaced = await Run(tool, new { from = "a.md", to = "b.md", overwrite = true });
        Assert.True(replaced.GetProperty("overwritten").GetBoolean());
        Assert.Equal("new", File.ReadAllText(target));

        Assert.Equal("not_found", (await Run(tool, new { from = "missing.md", to = "c.md" })).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("workspace_move", RiskLevel.Medium, RiskLevel.High)]
    [InlineData("workspace_copy", RiskLevel.Low, RiskLevel.Medium)]
    public void Move_and_copy_risk_rises_only_for_a_real_overwrite(string tool, RiskLevel normal, RiskLevel overwriting)
    {
        Put("a.md", "x");
        Put("b.md", "y");

        Assert.Equal(normal, Level(Assess(tool, new { from = "a.md", to = "c.md" })));
        Assert.Equal(normal, Level(Assess(tool, new { from = "a.md", to = "c.md", overwrite = true })));
        Assert.Equal(normal, Level(Assess(tool, new { from = "a.md", to = "b.md" }))); // fails with "exists" when run
        Assert.Equal(overwriting, Level(Assess(tool, new { from = "a.md", to = "b.md", overwrite = true })));
    }

    [Theory]
    [InlineData("workspace_move")]
    [InlineData("workspace_copy")]
    public void Either_sensitive_path_makes_move_and_copy_uncertain(string tool)
    {
        Put("run.sh", "x");
        Put("b.md", "y");

        Assert.IsType<ToolAssessment.Uncertain>(Assess(tool, new { from = "run.sh", to = "c.md" }));
        Assert.IsType<ToolAssessment.Uncertain>(Assess(tool, new { from = "b.md", to = ".profile" }));
        // Uncertain wins over the overwrite score.
        Assert.IsType<ToolAssessment.Uncertain>(Assess(tool, new { from = "b.md", to = "run.sh", overwrite = true }));
    }

    [Theory]
    [InlineData("workspace_move")]
    [InlineData("workspace_copy")]
    public void Move_and_copy_hardline_rules(string tool)
    {
        Put("a.md", "x");
        Directory.CreateDirectory(Path.Combine(_root, "dir"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "linkdir"), _outside);

        foreach (var (from, to) in new[]
                 {
                     ("/etc/passwd", "a2.md"), ("a.md", "/tmp/x.md"), ("../x.md", "a2.md"), ("a.md", "../x.md"),
                     ("linkdir/secret.txt", "s.txt"), ("a.md", "linkdir/a.md"), ("dir", "dir2"), ("a.md", "dir"),
                     ("a.md", "a.md"), ("", "a2.md"),
                 })
            Assert.IsType<ToolAssessment.Blocked>(Assess(tool, new { from, to }));

        Assert.IsType<ToolAssessment.Blocked>(Assess(tool, new { from = "a.md", to = "b.md", overwrite = "yes" }));
        Assert.Equal("files only", Assert.IsType<ToolAssessment.Blocked>(Assess(tool, new { from = "dir", to = "x" })).Reason);
    }

    [Fact]
    public void Copy_blocks_sources_over_the_size_cap()
    {
        Put("big.md", new string('x', WorkspaceTools.MaxWriteBytes + 1));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_copy", new { from = "big.md", to = "b.md" }));
        Assert.IsType<ToolAssessment.Scored>(Assess("workspace_move", new { from = "big.md", to = "b.md" }));
    }

    [Fact]
    public void Move_and_copy_summaries()
    {
        Assert.Equal("move a.md → archive/a.md (overwrite)", Summarize("workspace_move", new { from = "a.md", to = "archive/a.md", overwrite = true }));
        Assert.Equal("copy a.md → b.md", Summarize("workspace_copy", new { from = "a.md", to = "b.md" }));
    }
}
