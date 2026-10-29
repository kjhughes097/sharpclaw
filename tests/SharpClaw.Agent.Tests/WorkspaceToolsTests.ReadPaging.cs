using System.Text;
using SharpClaw.Agent.Tools;
using SharpClaw.Agent.Tools.Workspace;

namespace SharpClaw.Agent.Tests;

/// <summary>workspace_read paging (agent tools spec section 4.6).</summary>
public partial class WorkspaceToolsTests
{
    [Fact]
    public async Task Read_pages_round_trip_a_multibyte_file_without_corrupting_characters()
    {
        // 1-, 2-, 3- and 4-byte characters, so page ends land mid-character in every way.
        var original = string.Concat(Enumerable.Range(0, 30_000).Select(i => (i % 4) switch
        {
            0 => "a",
            1 => "é",
            2 => "€",
            _ => "😀",
        }));
        File.WriteAllText(Path.Combine(_root, "multi.txt"), original, new UTF8Encoding(false));
        var size = Encoding.UTF8.GetByteCount(original);

        var text = new StringBuilder();
        long offset = 0;
        var pages = 0;
        while (true)
        {
            var page = await Run("workspace_read", new { path = "multi.txt", offset });
            Assert.Equal(offset, page.GetProperty("offset").GetInt64());
            Assert.Equal(size, page.GetProperty("bytes").GetInt64());
            var content = page.GetProperty("content").GetString()!;
            Assert.DoesNotContain('\uFFFD', content);
            Assert.True(Encoding.UTF8.GetByteCount(content) <= WorkspaceTools.MaxReadBytes);
            text.Append(content);
            pages++;

            if (!page.GetProperty("truncated").GetBoolean())
            {
                Assert.False(page.TryGetProperty("nextOffset", out _));
                break;
            }
            offset = page.GetProperty("nextOffset").GetInt64();
        }

        Assert.Equal(original, text.ToString());
        Assert.True(pages > 1);
    }

    [Fact]
    public async Task Read_offset_snaps_forward_to_a_character_boundary()
    {
        File.WriteAllText(Path.Combine(_root, "e.txt"), "€uro", new UTF8Encoding(false)); // € is 3 bytes

        var page = await Run("workspace_read", new { path = "e.txt", offset = 1 });

        Assert.Equal(3, page.GetProperty("offset").GetInt64());
        Assert.Equal("uro", page.GetProperty("content").GetString());
        Assert.False(page.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Read_offset_past_the_end_returns_nothing()
    {
        Put("a.txt", "abc");
        var page = await Run("workspace_read", new { path = "a.txt", offset = 99 });
        Assert.Equal("", page.GetProperty("content").GetString());
        Assert.Equal(3, page.GetProperty("offset").GetInt64());
        Assert.False(page.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Read_first_page_reports_the_next_offset()
    {
        Put("big.txt", new string('y', WorkspaceTools.MaxReadBytes + 100));
        var page = await Run("workspace_read", new { path = "big.txt" });
        Assert.Equal(0, page.GetProperty("offset").GetInt64());
        Assert.Equal(WorkspaceTools.MaxReadBytes, page.GetProperty("nextOffset").GetInt64());
    }

    [Fact]
    public void Read_offset_is_validated_and_summarised()
    {
        Assert.Equal(RiskLevel.Low, Level(Assess("workspace_read", new { path = "a.txt", offset = 10 })));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_read", new { path = "a.txt", offset = -1 }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_read", new { path = "a.txt", offset = 1.5 }));
        Assert.IsType<ToolAssessment.Blocked>(Assess("workspace_read", new { path = "a.txt", offset = "10" }));
        Assert.Equal("read a.txt (from byte 10)", Summarize("workspace_read", new { path = "a.txt", offset = 10 }));
        Assert.Equal("read a.txt", Summarize("workspace_read", new { path = "a.txt" }));
    }
}
