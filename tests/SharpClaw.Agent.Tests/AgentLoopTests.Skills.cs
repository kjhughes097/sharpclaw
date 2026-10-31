using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SharpClaw.Agent.Skills;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Tests;

/// <summary>The skill index in the system prompt (skills spec section 6).</summary>
public partial class AgentLoopTests
{
    private static SkillLibrary SkillsIn(string root, params string[] names)
    {
        foreach (var name in names)
        {
            Directory.CreateDirectory(Path.Combine(root, name));
            File.WriteAllText(Path.Combine(root, name, "SKILL.md"), $"---\nname: {name}\ndescription: About {name}.\n---\nbody\n");
        }
        return SkillLibrary.Load(root, NullLogger.Instance);
    }

    private static FakeGatewayClient OneMessage() => new()
    {
        NextInbox = InboxResult.Of(new InboxMessageDto(Guid.NewGuid(), Guid.NewGuid(), "hi", TestClock.Now)),
        NextChat = ChatOutcome.Ok("r"),
    };

    private static AgentLoop LoopWithSkills(IGatewayClient client, string soul, SkillLibrary? skills) =>
        new(client, new FixedSoul(soul), Config(), NullLogger<AgentLoop>.Instance, new FakeTimeProvider(), skills: skills);

    [Fact]
    public async Task Skill_index_is_appended_after_the_soul_in_one_system_message()
    {
        var root = Path.Combine(Path.GetTempPath(), $"skills-{Guid.NewGuid():N}");
        try
        {
            var client = OneMessage();
            await LoopWithSkills(client, "SOUL\n", SkillsIn(root, "daily-notes")).ProcessOnceAsync(CancellationToken.None);

            var system = Assert.Single(client.LastChatMessages!, m => m.Role == Roles.System);
            Assert.Equal(Roles.System, client.LastChatMessages![0].Role);
            Assert.StartsWith("SOUL\n\n## Skills\n", system.Content);
            Assert.EndsWith("- daily-notes: About daily-notes.\n", system.Content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task No_index_without_skills()
    {
        var root = Path.Combine(Path.GetTempPath(), $"skills-{Guid.NewGuid():N}");
        var client = OneMessage();

        // An empty library (no directory) and no library at all (skill_read disabled) both leave the soul alone.
        await LoopWithSkills(client, "SOUL", SkillLibrary.Load(root, NullLogger.Instance)).ProcessOnceAsync(CancellationToken.None);
        Assert.Equal("SOUL", client.LastChatMessages![0].Content);

        await LoopWithSkills(client, "SOUL", null).ProcessOnceAsync(CancellationToken.None);
        Assert.Equal("SOUL", client.LastChatMessages![0].Content);
    }

    [Fact]
    public async Task Reserve_tokens_cover_the_soul_and_the_index()
    {
        var root = Path.Combine(Path.GetTempPath(), $"skills-{Guid.NewGuid():N}");
        try
        {
            var skills = SkillsIn(root, "daily-notes");
            var client = OneMessage();
            await LoopWithSkills(client, "SOUL", skills).ProcessOnceAsync(CancellationToken.None);

            var systemChars = ("SOUL\n\n" + skills.PromptIndex).Length;
            Assert.Equal(ContextDefaults.Tokens(systemChars) + ContextDefaults.MessageOverheadTokens, client.LastReserveTokens);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Index_is_built_once_at_startup()
    {
        var root = Path.Combine(Path.GetTempPath(), $"skills-{Guid.NewGuid():N}");
        try
        {
            var client = OneMessage();
            var loop = LoopWithSkills(client, "SOUL", SkillsIn(root, "first"));
            SkillsIn(root, "added-later");

            await loop.ProcessOnceAsync(CancellationToken.None);

            Assert.Contains("- first:", client.LastChatMessages![0].Content);
            Assert.DoesNotContain("added-later", client.LastChatMessages![0].Content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
