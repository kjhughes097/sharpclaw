using System.Text.Json;
using SharpClaw.Agent.Tools;
using SharpClaw.Agent.Tools.Workspace;

namespace SharpClaw.Agent.Tests;

public partial class WorkspaceToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ws-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
    private readonly Dictionary<string, IAgentTool> _tools;

    public WorkspaceToolsTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "outside secret");
        _tools = WorkspaceTools.All(_root).ToDictionary(t => t.Name);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    private static JsonElement Args(object args) => JsonSerializer.SerializeToElement(args);

    private ToolAssessment Assess(string tool, object args) => _tools[tool].Assess(Args(args));

    private async Task<JsonElement> Run(string tool, object args)
    {
        var result = await _tools[tool].ExecuteUncheckedAsync(Args(args), CancellationToken.None);
        return JsonDocument.Parse(result.Json).RootElement;
    }

    private string Summarize(string tool, object args) => _tools[tool].Summarize(Args(args));

    /// <summary>Creates a file (and its folders) under the workspace root.</summary>
    private string Put(string relative, string content)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private static RiskLevel Level(ToolAssessment assessment) => Assert.IsType<ToolAssessment.Scored>(assessment).Level;

    [Fact]
    public void Names_match_the_tools() =>
        Assert.Equal(WorkspaceTools.Names.OrderBy(n => n), _tools.Keys.OrderBy(n => n));

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../secret.txt")]
    [InlineData("notes/../../secret.txt")]
    [InlineData("..\\secret.txt")]
    [InlineData("")]
    [InlineData(".")]
    public void Paths_outside_or_at_the_root_are_blocked(string path)
    {
        foreach (var tool in new[] { "workspace_read", "workspace_write", "workspace_delete" })
            Assert.IsType<ToolAssessment.Blocked>(Assess(tool, new { path, content = "x" }));
    }

    [Fact]
    public void Missing_path_is_blocked_but_list_defaults_to_the_root()
    {
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_read", new { }));
        Assert.IsType<ToolAssessment.Scored>(Assess("workspace_list", new { }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_list", new { path = "../" }));
    }

    [Fact]
    public void Symbolic_links_are_blocked_anywhere_in_the_path()
    {
        Directory.CreateSymbolicLink(Path.Combine(_root, "linkdir"), _outside);
        File.CreateSymbolicLink(Path.Combine(_root, "link.txt"), Path.Combine(_outside, "secret.txt"));
        File.CreateSymbolicLink(Path.Combine(_root, "dangling.txt"), Path.Combine(_outside, "missing.txt"));

        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_read", new { path = "linkdir/secret.txt" }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_read", new { path = "link.txt" }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_write", new { path = "linkdir/new.txt", content = "x" }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_write", new { path = "dangling.txt", content = "x" }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_delete", new { path = "link.txt" }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_list", new { path = "linkdir" }));
    }

    [Fact]
    public async Task Write_then_read_round_trips_and_overwrite_is_medium()
    {
        var create = Assess("workspace_write", new { path = "notes/a.md", content = "hello" });
        Assert.Equal(RiskLevel.Low, Assert.IsType<ToolAssessment.Scored>(create).Level);
        Assert.Equal("write notes/a.md (5 B, new)", _tools["workspace_write"].Summarize(Args(new { path = "notes/a.md", content = "hello" })));

        var written = await Run("workspace_write", new { path = "notes/a.md", content = "hello" });
        Assert.True(written.GetProperty("ok").GetBoolean());
        Assert.True(written.GetProperty("created").GetBoolean());
        Assert.Equal("notes/a.md", written.GetProperty("path").GetString());

        var overwrite = Assess("workspace_write", new { path = "notes/a.md", content = "again" });
        Assert.Equal(RiskLevel.Medium, Assert.IsType<ToolAssessment.Scored>(overwrite).Level);
        Assert.False((await Run("workspace_write", new { path = "notes/a.md", content = "again" })).GetProperty("created").GetBoolean());

        var read = await Run("workspace_read", new { path = "notes/a.md" });
        Assert.Equal("again", read.GetProperty("content").GetString());
        Assert.False(read.GetProperty("truncated").GetBoolean());

        // The atomic write leaves no temporary files behind.
        Assert.Equal(new[] { "a.md" }, Directory.GetFiles(Path.Combine(_root, "notes")).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("run.sh")]
    [InlineData("tools/build.PS1")]
    [InlineData(".bashrc")]
    [InlineData(".config/settings.json")]
    [InlineData("app.py")]
    public void Sensitive_writes_are_uncertain(string path) =>
        Assert.IsType<ToolAssessment.Uncertain>(Assess("workspace_write", new { path, content = "x" }));

    [Fact]
    public void Write_caps_and_content_are_enforced()
    {
        Directory.CreateDirectory(Path.Combine(_root, "dir"));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_write", new { path = "big.txt", content = new string('x', WorkspaceTools.MaxWriteBytes + 1) }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_write", new { path = "a.txt" }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_write", new { path = "dir", content = "x" }));
        Assert.IsType<ToolAssessment.Scored>(Assess("workspace_write", new { path = "ok.txt", content = new string('x', WorkspaceTools.MaxWriteBytes) }));
    }

    [Fact]
    public async Task Large_files_are_truncated_on_read()
    {
        File.WriteAllText(Path.Combine(_root, "big.txt"), new string('y', WorkspaceTools.MaxReadBytes + 100));

        var read = await Run("workspace_read", new { path = "big.txt" });

        Assert.Equal(WorkspaceTools.MaxReadBytes, read.GetProperty("content").GetString()!.Length);
        Assert.True(read.GetProperty("truncated").GetBoolean());
        Assert.True(_tools["workspace_read"].ReturnsUntrustedContent);
    }

    [Fact]
    public async Task List_shows_entries_and_caps_the_count()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        for (var i = 0; i < WorkspaceTools.MaxListEntries + 5; i++)
            File.WriteAllText(Path.Combine(_root, "sub", $"f{i:000}.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "top.txt"), "abc");

        var root = await Run("workspace_list", new { });
        var entries = root.GetProperty("entries").EnumerateArray().ToList();
        Assert.Contains(entries, e => e.GetProperty("name").GetString() == "sub" && e.GetProperty("type").GetString() == "directory");
        Assert.Contains(entries, e => e.GetProperty("name").GetString() == "top.txt" && e.GetProperty("size").GetInt64() == 3);

        var sub = await Run("workspace_list", new { path = "sub" });
        Assert.Equal(WorkspaceTools.MaxListEntries, sub.GetProperty("entries").GetArrayLength());
        Assert.True(sub.GetProperty("truncated").GetBoolean());

        Assert.Equal("not_found", (await Run("workspace_list", new { path = "nope" })).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Delete_is_high_risk_and_files_only()
    {
        File.WriteAllText(Path.Combine(_root, "old.md"), "x");
        Directory.CreateDirectory(Path.Combine(_root, "folder"));

        Assert.Equal(RiskLevel.High, Assert.IsType<ToolAssessment.Scored>(Assess("workspace_delete", new { path = "old.md" })).Level);
        Assert.Equal("delete old.md", _tools["workspace_delete"].Summarize(Args(new { path = "old.md" })));

        Assert.True((await Run("workspace_delete", new { path = "old.md" })).GetProperty("deleted").GetBoolean());
        Assert.False(File.Exists(Path.Combine(_root, "old.md")));
        Assert.Equal("not_found", (await Run("workspace_delete", new { path = "old.md" })).GetProperty("error").GetString());
        Assert.Equal("not_a_file", (await Run("workspace_delete", new { path = "folder" })).GetProperty("error").GetString());
    }

    [Fact]
    public void Summaries_never_include_content()
    {
        var summary = _tools["workspace_write"].Summarize(Args(new { path = "a.md", content = "SECRET_CONTENT" }));
        Assert.DoesNotContain("SECRET_CONTENT", summary);
        Assert.True(summary.Length <= 200);
    }

    [Fact]
    public void Every_tool_has_a_valid_parameter_schema()
    {
        foreach (var tool in _tools.Values)
        {
            using var schema = JsonDocument.Parse(tool.ParametersJson);
            Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        }
    }
}
