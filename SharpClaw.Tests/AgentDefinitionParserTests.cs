using SharpClaw.Loading;
using SharpClaw.Models;

namespace SharpClaw.Tests;

public class AgentDefinitionParserTests
{
    [Fact]
    public void Parse_extracts_frontmatter_and_body()
    {
        var tempFile = Path.GetTempFileName();
        var newPath = Path.ChangeExtension(tempFile, ".agent.md");
        // Rename: parser derives name from filename
        var dir = Path.GetDirectoryName(tempFile)!;
        var filePath = Path.Combine(dir, "testbot.agent.md");

        File.WriteAllText(filePath, """
            ---
            description: A test bot
            llm: copilot
            model: gpt-4o
            tools: [echo, spawn_agent]
            mcp_servers:
              - memory
            skills: []
            sub_agents: [cody]
            ---

            You are a test bot.
            """);

        try
        {
            var result = AgentDefinitionParser.Parse(filePath);

            Assert.Equal("testbot", result.Name);
            Assert.Equal("A test bot", result.Description);
            Assert.Equal("copilot", result.Llm);
            Assert.Equal("gpt-4o", result.Model);
            Assert.Equal(["echo", "spawn_agent"], result.ToolNames);
            Assert.Equal(["memory"], result.McpNames);
            Assert.Empty(result.SkillNames);
            Assert.Equal(["cody"], result.SubAgentNames);
            Assert.Contains("You are a test bot", result.SystemPrompt);
        }
        finally
        {
            File.Delete(filePath);
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Parse_no_frontmatter_returns_whole_body_as_prompt()
    {
        var dir = Path.GetTempPath();
        var filePath = Path.Combine(dir, "simple.agent.md");
        File.WriteAllText(filePath, "Just a simple prompt.");

        try
        {
            var result = AgentDefinitionParser.Parse(filePath);

            Assert.Equal("simple", result.Name);
            Assert.Null(result.Description);
            Assert.Null(result.Llm);
            Assert.Null(result.Model);
            Assert.Empty(result.ToolNames);
            Assert.Contains("Just a simple prompt", result.SystemPrompt);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Parse_extracts_llm_field_as_anthropic()
    {
        var dir = Path.GetTempPath();
        var filePath = Path.Combine(dir, "claude.agent.md");
        File.WriteAllText(filePath, """
            ---
            llm: anthropic
            model: claude-sonnet-4-20250514
            description: Claude agent
            ---

            You are Claude.
            """);

        try
        {
            var result = AgentDefinitionParser.Parse(filePath);

            Assert.Equal("claude", result.Name);
            Assert.Equal("anthropic", result.Llm);
            Assert.Equal("claude-sonnet-4-20250514", result.Model);
            Assert.Equal("Claude agent", result.Description);
            Assert.Contains("You are Claude", result.SystemPrompt);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Parse_llm_defaults_to_null_when_omitted()
    {
        var dir = Path.GetTempPath();
        var filePath = Path.Combine(dir, "nofield.agent.md");
        File.WriteAllText(filePath, """
            ---
            model: gpt-4o
            ---

            A prompt.
            """);

        try
        {
            var result = AgentDefinitionParser.Parse(filePath);

            Assert.Null(result.Llm);
            Assert.Equal("gpt-4o", result.Model);
        }
        finally
        {
            File.Delete(filePath);
        }
    }
}
