using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Agent.Skills;
using SharpClaw.Agent.Tools;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Tests.Skills;

public class SkillReadToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"skills-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
    private readonly string _skillMd = "---\nname: research-brief\ndescription: Write briefs.\n---\n# Research brief\nLoad assets/brief-template.md.\n";
    private readonly IAgentTool _tool;

    public SkillReadToolTests()
    {
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "outside secret");
        Put("SKILL.md", _skillMd);
        Put("references/style-guide.md", "Be brief.");
        Put("assets/brief-template.md", "# Title");
        _tool = SkillTools.All(SkillLibrary.Load(_root, NullLogger.Instance)).Single();
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    private string Put(string relative, string content, string skill = "research-brief")
    {
        var full = Path.Combine(_root, skill, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private static JsonElement Args(object args) => JsonSerializer.SerializeToElement(args);

    private async Task<JsonElement> Run(object args)
    {
        var result = await _tool.ExecuteUncheckedAsync(Args(args), CancellationToken.None);
        return JsonDocument.Parse(result.Json).RootElement;
    }

    private string Blocked(object args) => Assert.IsType<ToolAssessment.Blocked>(_tool.Assess(Args(args))).Reason;

    [Fact]
    public void Is_a_low_risk_trusted_tool()
    {
        Assert.Equal("skill_read", _tool.Name);
        Assert.False(_tool.ReturnsUntrustedContent);
        var scored = Assert.IsType<ToolAssessment.Scored>(_tool.Assess(Args(new { name = "research-brief" })));
        Assert.Equal(RiskLevel.Low, scored.Level);
    }

    [Fact]
    public async Task Default_path_is_SKILL_md_with_frontmatter_and_a_file_list()
    {
        var r = await Run(new { name = "research-brief" });

        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal("research-brief", r.GetProperty("name").GetString());
        Assert.Equal("SKILL.md", r.GetProperty("path").GetString());
        Assert.Equal(_skillMd, r.GetProperty("content").GetString());
        Assert.Equal(0, r.GetProperty("offset").GetInt64());
        Assert.False(r.GetProperty("truncated").GetBoolean());
        Assert.False(r.TryGetProperty("nextOffset", out _));
        Assert.Equal(new[] { "assets/brief-template.md", "references/style-guide.md" },
            r.GetProperty("files").EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public async Task File_list_skips_symlinks_and_is_capped()
    {
        File.CreateSymbolicLink(Path.Combine(_root, "research-brief", "link.md"), Path.Combine(_outside, "secret.txt"));
        for (var i = 0; i < SkillTools.MaxListedFiles + 5; i++)
            Put($"many/f{i:000}.md", "x");

        var files = (await Run(new { name = "research-brief" })).GetProperty("files").EnumerateArray().Select(f => f.GetString()).ToList();

        Assert.Equal(SkillTools.MaxListedFiles, files.Count);
        Assert.DoesNotContain("link.md", files);
    }

    [Fact]
    public async Task Nested_path_has_no_file_list()
    {
        var r = await Run(new { name = "research-brief", path = "references/style-guide.md" });
        Assert.Equal("Be brief.", r.GetProperty("content").GetString());
        Assert.Equal("references/style-guide.md", r.GetProperty("path").GetString());
        Assert.False(r.TryGetProperty("files", out _));
    }

    [Fact]
    public async Task Unknown_skill_is_an_error()
    {
        Assert.IsType<ToolAssessment.Scored>(_tool.Assess(Args(new { name = "nope" })));
        var r = await Run(new { name = "nope" });
        Assert.Equal("unknown_skill", r.GetProperty("error").GetString());
    }

    [Fact]
    public void Paths_outside_the_skill_are_blocked()
    {
        Put("SKILL.md", "---\nname: other\ndescription: d\n---\n", skill: "other");

        Assert.Contains("outside the skill", Blocked(new { name = "research-brief", path = "../other/SKILL.md" }));
        Assert.Contains("outside the skill", Blocked(new { name = "research-brief", path = "references/../../other/SKILL.md" }));
        Assert.Contains("absolute", Blocked(new { name = "research-brief", path = "/etc/passwd" }));
        Assert.Contains("absolute", Blocked(new { name = "research-brief", path = Path.Combine(_outside, "secret.txt") }));
        Assert.Contains("inside the skill", Blocked(new { name = "research-brief", path = "" }));
        Assert.Contains("must be a string", Blocked(new { name = "research-brief", path = 3 }));
        Assert.Contains("offset", Blocked(new { name = "research-brief", offset = -1 }));
    }

    [Fact]
    public void Symlink_components_are_blocked()
    {
        File.CreateSymbolicLink(Path.Combine(_root, "research-brief", "secret.txt"), Path.Combine(_outside, "secret.txt"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "research-brief", "out"), _outside);

        Assert.Contains("symbolic link", Blocked(new { name = "research-brief", path = "secret.txt" }));
        Assert.Contains("symbolic link", Blocked(new { name = "research-brief", path = "out/secret.txt" }));
    }

    [Fact]
    public async Task Directory_missing_and_binary_files_are_errors()
    {
        File.WriteAllBytes(Path.Combine(_root, "research-brief", "image.png"), new byte[] { 0x89, 0x50, 0x00, 0x47 });

        Assert.Equal("not_a_file", (await Run(new { name = "research-brief", path = "references" })).GetProperty("error").GetString());
        Assert.Equal("not_found", (await Run(new { name = "research-brief", path = "nope.md" })).GetProperty("error").GetString());
        Assert.Equal("binary_file", (await Run(new { name = "research-brief", path = "image.png" })).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Paging_round_trips_multi_byte_utf8()
    {
        // 3-byte characters, so the 64 KB page boundary falls mid-character.
        var text = string.Concat(Enumerable.Repeat("€a", 20_000));
        Put("references/long.md", text);

        var sb = new StringBuilder();
        long? offset = 0;
        var pages = 0;
        while (offset is not null)
        {
            var r = await Run(new { name = "research-brief", path = "references/long.md", offset });
            sb.Append(r.GetProperty("content").GetString());
            offset = r.GetProperty("truncated").GetBoolean() ? r.GetProperty("nextOffset").GetInt64() : null;
            pages++;
        }

        Assert.Equal(2, pages);
        Assert.Equal(text, sb.ToString());
    }

    [Fact]
    public async Task Edits_are_visible_without_a_restart()
    {
        Put("references/style-guide.md", "Be very brief.");
        var r = await Run(new { name = "research-brief", path = "references/style-guide.md" });
        Assert.Equal("Be very brief.", r.GetProperty("content").GetString());
    }

    [Fact]
    public void Summary_has_only_name_and_path()
    {
        Assert.Equal("read skill research-brief/SKILL.md", _tool.Summarize(Args(new { name = "research-brief" })));
        Assert.Equal("read skill research-brief/references/style-guide.md",
            _tool.Summarize(Args(new { name = "research-brief", path = "references/style-guide.md", offset = 10 })));
    }

    [Fact]
    public async Task Dispatched_reads_do_not_taint_the_turn()
    {
        var gateway = new RecordingGateway();
        var config = new AgentToolConfig(RiskLevel.Low, false, 10, SkillTools.Names.ToArray());
        var dispatcher = new ToolDispatcher(new ToolRegistry(new[] { _tool }, config.Tools), config, gateway,
            new ListLogger<ToolDispatcher>());
        var turn = new TurnContext(Guid.NewGuid(), Guid.NewGuid());

        var result = await dispatcher.DispatchAsync(
            new ToolCallRequest("c1", "skill_read", """{"name":"research-brief"}"""), turn, default);

        Assert.True(result.Ok);
        Assert.False(result.Untrusted);
        Assert.False(turn.Tainted);
        Assert.Empty(gateway.ApprovalRequests);
        var report = Assert.Single(gateway.Reports);
        Assert.Equal("c1", report.ToolCallId);
        Assert.False(report.Tainted);
    }
}
