using SharpClaw.Agent.Skills;

namespace SharpClaw.Agent.Tests.Skills;

public class SkillFrontmatterTests
{
    private static SkillFrontmatter.Fields Ok(string text)
    {
        var (fields, error) = SkillFrontmatter.Parse(text);
        Assert.Null(error);
        return fields!;
    }

    private static string Error(string text)
    {
        var (fields, error) = SkillFrontmatter.Parse(text);
        Assert.Null(fields);
        return error!;
    }

    [Fact]
    public void Plain_values()
    {
        var f = Ok("---\nname: daily-notes\ndescription: Keep notes. Use when asked.\n---\n# Body\n");
        Assert.Equal("daily-notes", f.Name);
        Assert.Equal("Keep notes. Use when asked.", f.Description);
    }

    [Fact]
    public void Crlf_bom_and_trailing_comment_are_handled()
    {
        var f = Ok("\uFEFF---\r\nname: a-b # the name\r\ndescription: Does x\r\n---\r\nbody");
        Assert.Equal("a-b", f.Name);
        Assert.Equal("Does x", f.Description);
    }

    [Fact]
    public void Quoted_values()
    {
        var f = Ok("---\nname: 'x'\ndescription: \"Say \\\"hi\\\": it's \\u00e9 fine\"\n---\n");
        Assert.Equal("x", f.Name);
        Assert.Equal("Say \"hi\": it's é fine", f.Description);

        Assert.Equal("it's: ok", Ok("---\ndescription: 'it''s: ok'\n---\n").Description);
    }

    [Fact]
    public void Folded_block_description_joins_lines()
    {
        var f = Ok("---\nname: x\ndescription: >\n  Keep a dated notes file.\n  Use when asked.\n---\n");
        Assert.Equal("Keep a dated notes file. Use when asked.", f.Description);
    }

    [Fact]
    public void Literal_block_description_keeps_lines()
    {
        var f = Ok("---\nname: x\ndescription: |-\n  Line one\n  # not a comment\n\n  Line three\n---\n");
        Assert.Equal("Line one\n# not a comment\n\nLine three", f.Description);
    }

    [Fact]
    public void Unknown_fields_are_ignored_even_when_nested()
    {
        var f = Ok("""
            ---
            name: x
            license: MIT
            allowed-tools: [workspace_delete, sandbox_run]
            metadata:
              author: someone
              tags:
                - a
            other:
            - item
            description: d
            ---
            """);
        Assert.Equal("x", f.Name);
        Assert.Equal("d", f.Description);
    }

    [Fact]
    public void Missing_fields_are_null()
    {
        var f = Ok("---\nlicense: MIT\n---\n");
        Assert.Null(f.Name);
        Assert.Null(f.Description);
    }

    [Theory]
    [InlineData("# Title\nno frontmatter")]
    [InlineData("\n---\nname: x\n---\n")]
    [InlineData("")]
    public void Frontmatter_must_come_first(string text) =>
        Assert.Contains("must start with", Error(text));

    [Fact]
    public void Unclosed_frontmatter_is_invalid() =>
        Assert.Contains("not closed", Error("---\nname: x\ndescription: d\n# body"));

    [Fact]
    public void Oversized_frontmatter_is_invalid()
    {
        var text = "---\nname: x\nlicense: " + new string('a', SkillFrontmatter.MaxBytes) + "\n---\n";
        Assert.Contains("larger than 4 KB", Error(text));
    }

    [Theory]
    [InlineData("name: &anchor x")]
    [InlineData("name: *alias")]
    [InlineData("name: !!str x")]
    [InlineData("name: [a, b]")]
    [InlineData("name: {a: b}")]
    [InlineData("name: >\n  x")]
    [InlineData("name: x\n  continued")]
    [InlineData("description: a: b")]
    [InlineData("description: \"unclosed")]
    [InlineData("description: 'a' trailing")]
    [InlineData("description: \"bad \\x41 escape\"")]
    [InlineData("description: >2\n  x")]
    [InlineData("name: x\nname: y")]
    [InlineData("just text")]
    [InlineData("  indented: x")]
    [InlineData("description: |\n- item")]
    public void Unsupported_constructs_in_read_fields_are_invalid(string body) =>
        Assert.NotNull(Error($"---\n{body}\n---\n"));

    [Fact]
    public void Error_reasons_never_quote_content()
    {
        var error = Error("---\ndescription: SECRET: stuff\n---\n");
        Assert.DoesNotContain("SECRET", error);
    }
}
