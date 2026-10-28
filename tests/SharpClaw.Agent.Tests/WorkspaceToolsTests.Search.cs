using System.Text.Json;
using SharpClaw.Agent.Tools;
using SharpClaw.Agent.Tools.Workspace;

namespace SharpClaw.Agent.Tests;

/// <summary>workspace_search and workspace_find (agent tools spec sections 4.1 and 4.2).</summary>
public partial class WorkspaceToolsTests
{
    private static List<string> Paths(JsonElement result) =>
        result.GetProperty("paths").EnumerateArray().Select(p => p.GetString()!).ToList();

    private static List<(string Path, int Line, string Text)> Matches(JsonElement result) =>
        result.GetProperty("matches").EnumerateArray()
            .Select(m => (m.GetProperty("path").GetString()!, m.GetProperty("line").GetInt32(), m.GetProperty("text").GetString()!))
            .ToList();

    [Fact]
    public async Task Search_finds_literal_text_case_insensitively_with_line_numbers()
    {
        Put("notes/a.md", "first line\n  Hello World  \nnothing\nhello again");
        Put("notes/b.txt", "HELLO from txt");
        Put("other.md", "no match here");

        var result = await Run("workspace_search", new { query = "hello" });

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.Equal(new[] { ("notes/a.md", 2, "Hello World"), ("notes/a.md", 4, "hello again"), ("notes/b.txt", 1, "HELLO from txt") },
            Matches(result));
        Assert.True(_tools["workspace_search"].ReturnsUntrustedContent);
    }

    [Fact]
    public async Task Search_is_literal_not_regex()
    {
        Put("a.md", "price is $5.00 (approx)\nprice is 5x00");
        var matches = Matches(await Run("workspace_search", new { query = "5.00 (" }));
        Assert.Equal(1, Assert.Single(matches).Line);
    }

    [Fact]
    public async Task Search_filters_by_directory_and_glob()
    {
        Put("notes/a.md", "needle");
        Put("notes/deep/b.md", "needle");
        Put("notes/c.txt", "needle");
        Put("top.md", "needle");

        var byName = Matches(await Run("workspace_search", new { query = "needle", path = "notes", glob = "*.md" }));
        Assert.Equal(new[] { "notes/a.md", "notes/deep/b.md" }, byName.Select(m => m.Path));

        var byPath = Matches(await Run("workspace_search", new { query = "needle", path = "notes", glob = "deep/*.md" }));
        Assert.Equal("notes/deep/b.md", Assert.Single(byPath).Path);

        Assert.Equal("not_found", (await Run("workspace_search", new { query = "x", path = "missing" })).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Search_skips_binary_and_oversized_files_and_trims_long_lines()
    {
        Put("long.md", "  " + "needle" + new string('z', 500));
        File.WriteAllBytes(Path.Combine(_root, "bin.dat"), "needle\0needle"u8.ToArray());
        Put("huge.md", "needle\n" + new string('y', (int)SearchTool.MaxFileBytes));

        var match = Assert.Single(Matches(await Run("workspace_search", new { query = "NEEDLE" })));

        Assert.Equal("long.md", match.Path);
        Assert.Equal(SearchTool.MaxLineChars, match.Text.Length);
        Assert.StartsWith("needle", match.Text);
    }

    [Fact]
    public async Task Search_caps_matches()
    {
        Put("many.md", string.Join('\n', Enumerable.Repeat("needle", SearchTool.MaxMatches + 1)));
        var result = await Run("workspace_search", new { query = "needle" });
        Assert.Equal(SearchTool.MaxMatches, Matches(result).Count);
        Assert.True(result.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Search_caps_files_visited()
    {
        for (var i = 0; i <= SearchTool.MaxFilesVisited; i++)
            Put($"f/{i:0000}.md", "x");
        Put("f/9999.md", "needle"); // beyond the cap in walk order

        var result = await Run("workspace_search", new { query = "needle" });

        Assert.Empty(Matches(result));
        Assert.True(result.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Search_and_find_never_follow_symbolic_links()
    {
        File.WriteAllText(Path.Combine(_outside, "secret.md"), "needle outside");
        Directory.CreateSymbolicLink(Path.Combine(_root, "linkdir"), _outside);
        File.CreateSymbolicLink(Path.Combine(_root, "link.md"), Path.Combine(_outside, "secret.md"));
        Put("inside.md", "needle inside");

        var matches = Matches(await Run("workspace_search", new { query = "needle" }));
        Assert.Equal("inside.md", Assert.Single(matches).Path);
        Assert.Equal(new[] { "inside.md" }, Paths(await Run("workspace_find", new { pattern = "**/*.md" })));

        // A symlink as the starting directory is a hardline block.
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_search", new { query = "needle", path = "linkdir" }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_find", new { pattern = "*", path = "linkdir" }));
    }

    [Theory]
    [InlineData("workspace_search")]
    [InlineData("workspace_find")]
    public void Search_and_find_block_paths_outside_the_workspace(string tool)
    {
        foreach (var path in new[] { "/etc", "..", "notes/../../x" })
            Assert.IsType<ToolAssessment.Blocked>(Assess(tool, new { query = "x", pattern = "*", path }));
        Assert.Equal(RiskLevel.Low, Level(Assess(tool, new { query = "x", pattern = "*" })));
    }

    [Fact]
    public void Search_and_find_validate_their_arguments()
    {
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_search", new { }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_search", new { query = "" }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_search", new { query = new string('q', SearchTool.MaxQueryLength + 1) }));
        Assert.IsType<ToolAssessment.Scored>(Assess("workspace_search", new { query = new string('q', SearchTool.MaxQueryLength) }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_search", new { query = "x", glob = "/etc/*" }));

        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_find", new { }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_find", new { pattern = "/abs/*.md" }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_find", new { pattern = "../*.md" }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_find", new { pattern = new string('*', Glob.MaxPatternLength + 1) }));
    }

    [Fact]
    public void Search_and_find_summaries_hold_no_query()
    {
        var search = Summarize("workspace_search", new { query = "SECRET_QUERY", path = "notes", glob = "*.md" });
        Assert.Equal("search notes/ (*.md)", search);
        Assert.Equal("search ./", Summarize("workspace_search", new { query = "SECRET_QUERY" }));
        Assert.Equal("find **/*.md in notes/", Summarize("workspace_find", new { pattern = "**/*.md", path = "notes/" }));
    }

    [Fact]
    public async Task Find_matches_globs_relative_to_the_directory_sorted_without_directories()
    {
        Put("b.md", "");
        Put("A.MD", "");
        Put("notes/c.md", "");
        Put("notes/sub/d.md", "");
        Put("notes/e.txt", "");
        Directory.CreateDirectory(Path.Combine(_root, "dir.md"));

        Assert.Equal(new[] { "A.MD", "b.md" }, Paths(await Run("workspace_find", new { pattern = "*.md" })));
        Assert.Equal(new[] { "A.MD", "b.md", "notes/c.md", "notes/sub/d.md" }, Paths(await Run("workspace_find", new { pattern = "**/*.md" })));
        Assert.Equal(new[] { "notes/sub/d.md" }, Paths(await Run("workspace_find", new { pattern = "sub/?.md", path = "notes" })));
        Assert.Equal(new[] { "notes/e.txt" }, Paths(await Run("workspace_find", new { pattern = "notes/**/*.t*" })));
        Assert.Equal("not_found", (await Run("workspace_find", new { pattern = "*", path = "missing" })).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Find_caps_results()
    {
        for (var i = 0; i < FindTool.MaxResults + 3; i++)
            Put($"many/{i:000}.md", "");

        var result = await Run("workspace_find", new { pattern = "**/*.md" });

        Assert.Equal(FindTool.MaxResults, Paths(result).Count);
        Assert.Equal("many/000.md", Paths(result)[0]);
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.False(_tools["workspace_find"].ReturnsUntrustedContent);
    }

    [Theory]
    [InlineData("**", "a/b/c.md", true)]
    [InlineData("**/*.md", "c.md", true)]
    [InlineData("a/**/c.md", "a/c.md", true)]
    [InlineData("a/**/c.md", "a/x/y/c.md", true)]
    [InlineData("*.md", "a/c.md", false)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("?.md", "ab.md", false)]
    [InlineData("**/**/**/**/**/**/x", "a/b/c/d/e/f/g/h/i/j/y", false)]
    public void Glob_matching(string pattern, string path, bool expected) =>
        Assert.Equal(expected, Glob.IsMatch(pattern, path));
}
