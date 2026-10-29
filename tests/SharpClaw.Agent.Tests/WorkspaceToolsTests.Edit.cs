using System.Text;
using SharpClaw.Agent.Tools;
using SharpClaw.Agent.Tools.Workspace;

namespace SharpClaw.Agent.Tests;

/// <summary>workspace_edit and workspace_append (agent tools spec sections 4.3 and 4.4).</summary>
public partial class WorkspaceToolsTests
{
    private string[] FilesIn(string relativeDir) =>
        Directory.GetFiles(Path.Combine(_root, relativeDir)).Select(f => Path.GetFileName(f)!).Order().ToArray();

    [Fact]
    public async Task Edit_replaces_exactly_one_match_atomically()
    {
        var full = Put("notes/a.md", "alpha\nbeta\ngamma\n");

        var result = await Run("workspace_edit", new { path = "notes/a.md", oldText = "beta\n", newText = "BETA!\n" });

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal("notes/a.md", result.GetProperty("path").GetString());
        Assert.Equal("alpha\nBETA!\ngamma\n", File.ReadAllText(full));
        Assert.Equal(new FileInfo(full).Length, result.GetProperty("bytes").GetInt64());
        Assert.Equal(new[] { "a.md" }, FilesIn("notes")); // no temp files left
    }

    [Fact]
    public async Task Edit_with_empty_new_text_deletes_the_match()
    {
        var full = Put("a.md", "keep DROP keep");
        await Run("workspace_edit", new { path = "a.md", oldText = " DROP", newText = "" });
        Assert.Equal("keep keep", File.ReadAllText(full));
    }

    [Fact]
    public async Task Edit_reports_no_match_and_ambiguous_match_without_changing_the_file()
    {
        var full = Put("a.md", "one two one");

        var none = await Run("workspace_edit", new { path = "a.md", oldText = "three", newText = "x" });
        Assert.Equal("no_match", none.GetProperty("error").GetString());

        var many = await Run("workspace_edit", new { path = "a.md", oldText = "one", newText = "x" });
        Assert.Equal("ambiguous_match", many.GetProperty("error").GetString());
        Assert.Contains("2 times", many.GetProperty("message").GetString());

        // Overlapping occurrences count as ambiguous too.
        Put("b.md", "aaa");
        Assert.Equal("ambiguous_match", (await Run("workspace_edit", new { path = "b.md", oldText = "aa", newText = "x" })).GetProperty("error").GetString());

        Assert.Equal("one two one", File.ReadAllText(full));
        Assert.Equal("not_found", (await Run("workspace_edit", new { path = "missing.md", oldText = "a", newText = "b" })).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Edit_enforces_text_and_size_caps()
    {
        Put("full.md", "x" + new string('y', WorkspaceTools.MaxWriteBytes - 1));
        var grow = await Run("workspace_edit", new { path = "full.md", oldText = "x", newText = "xx" });
        Assert.Equal("too_large", grow.GetProperty("error").GetString());
        Assert.Equal(WorkspaceTools.MaxWriteBytes, new FileInfo(Path.Combine(_root, "full.md")).Length);

        Put("huge.md", new string('y', WorkspaceTools.MaxWriteBytes + 1));
        Assert.Equal("too_large", (await Run("workspace_edit", new { path = "huge.md", oldText = "y", newText = "" })).GetProperty("error").GetString());

        File.WriteAllBytes(Path.Combine(_root, "bin.dat"), "ab\0cd"u8.ToArray());
        Assert.Equal("not_text", (await Run("workspace_edit", new { path = "bin.dat", oldText = "ab", newText = "x" })).GetProperty("error").GetString());
    }

    [Fact]
    public void Edit_assessment_is_medium_sensitive_uncertain_and_hardline_blocked()
    {
        Directory.CreateDirectory(Path.Combine(_root, "dir"));
        Assert.Equal(RiskLevel.Medium, Level(Assess("workspace_edit", new { path = "a.md", oldText = "a", newText = "b" })));
        Assert.IsType<ToolAssessment.Uncertain>(Assess("workspace_edit", new { path = "run.sh", oldText = "a", newText = "b" }));
        Assert.IsType<ToolAssessment.Uncertain>(Assess("workspace_edit", new { path = ".config/x.json", oldText = "a", newText = "b" }));

        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_edit", new { path = "a.md", oldText = "", newText = "b" }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_edit", new { path = "a.md", oldText = "a" }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_edit", new { path = "dir", oldText = "a", newText = "b" }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_edit", new { path = "a.md", oldText = "a", newText = new string('n', WorkspaceTools.MaxWriteBytes + 1) }));
        foreach (var path in new[] { "/etc/passwd", "../x.md", "" })
            Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_edit", new { path, oldText = "a", newText = "b" }));
    }

    [Fact]
    public void Edit_and_append_summaries_hold_sizes_not_content()
    {
        var edit = Summarize("workspace_edit", new { path = "notes/a.md", oldText = new string('o', 120), newText = "SECRET_NEW" + new string('n', 330) });
        Assert.Equal("edit notes/a.md (−120 B, +340 B)", edit);

        var append = Summarize("workspace_append", new { path = "notes/log.md", content = "SECRET" + new string('c', 1223) });
        Assert.Equal("append notes/log.md (1.2 KB)", append);
    }

    [Fact]
    public async Task Append_creates_then_appends_without_separators()
    {
        var first = await Run("workspace_append", new { path = "logs/day.md", content = "one\n" });
        Assert.True(first.GetProperty("created").GetBoolean());
        Assert.Equal(4, first.GetProperty("bytes").GetInt64());

        var second = await Run("workspace_append", new { path = "logs/day.md", content = "two" });
        Assert.False(second.GetProperty("created").GetBoolean());
        Assert.Equal(7, second.GetProperty("bytes").GetInt64());

        Assert.Equal("one\ntwo", File.ReadAllText(Path.Combine(_root, "logs", "day.md"), Encoding.UTF8));
        Assert.Equal(new[] { "day.md" }, FilesIn("logs"));
    }

    [Fact]
    public async Task Append_enforces_caps()
    {
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_append", new { path = "a.md", content = new string('x', AppendTool.MaxAppendBytes + 1) }));
        Assert.IsType<ToolAssessment.Scored>(Assess("workspace_append", new { path = "a.md", content = new string('x', AppendTool.MaxAppendBytes) }));

        var full = Put("big.md", new string('y', WorkspaceTools.MaxWriteBytes - 1));
        Assert.Equal("too_large", (await Run("workspace_append", new { path = "big.md", content = "zz" })).GetProperty("error").GetString());
        Assert.Equal(WorkspaceTools.MaxWriteBytes - 1, new FileInfo(full).Length);
        Assert.True((await Run("workspace_append", new { path = "big.md", content = "z" })).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Append_assessment_is_low_sensitive_uncertain_and_hardline_blocked()
    {
        Directory.CreateDirectory(Path.Combine(_root, "dir"));
        File.CreateSymbolicLink(Path.Combine(_root, "link.md"), Path.Combine(_outside, "secret.txt"));

        Assert.Equal(RiskLevel.Low, Level(Assess("workspace_append", new { path = "log.md", content = "x" })));
        Assert.IsType<ToolAssessment.Uncertain>(Assess("workspace_append", new { path = ".bashrc", content = "x" }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_append", new { path = "log.md" }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_append", new { path = "dir", content = "x" }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_append", new { path = "link.md", content = "x" }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_append", new { path = "../x.md", content = "x" }));
    }
}
