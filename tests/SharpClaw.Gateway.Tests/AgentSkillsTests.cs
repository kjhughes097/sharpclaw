using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;

namespace SharpClaw.Gateway.Tests;

/// <summary><c>PUT /agent/skills</c> and the skills on <c>AgentDto</c> (skills spec section 9).</summary>
public class AgentSkillsTests
{
    private static object Skill(string name, string description = "Does a thing.") => new { name, description };

    private static Task<HttpResponseMessage> ReportAsync(HttpClient agent, params object[] skills) =>
        agent.PutAsJsonAsync("/agent/skills", new { skills });

    private static async Task<AgentDto> GetAgentAsync(GatewayFixture fx, string name)
    {
        using var admin = fx.Admin();
        return (await admin.GetFromJsonAsync<AgentDto>($"/api/agents/{name}", SharpClawJson.Options))!;
    }

    [Fact]
    public async Task Report_replaces_the_set_and_shows_on_the_agent_in_ordinal_order()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (_, token) = await fx.CreateAgentAsync();
        using var agent = fx.AgentClient(token);

        var before = await GetAgentAsync(fx, "assistant");
        Assert.Empty(before.Skills);
        Assert.Null(before.SkillsReportedAt);

        Assert.Equal(HttpStatusCode.NoContent, (await ReportAsync(agent, Skill("research-brief"), Skill("daily-notes", "  Keep notes.  "))).StatusCode);
        var first = await GetAgentAsync(fx, "assistant");
        Assert.Equal(new[] { new AgentSkillDto("daily-notes", "Keep notes."), new AgentSkillDto("research-brief", "Does a thing.") }, first.Skills);
        Assert.Equal(fx.Time.GetUtcNow().UtcDateTime, first.SkillsReportedAt);

        fx.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(HttpStatusCode.NoContent, (await ReportAsync(agent, Skill("csv-summary"))).StatusCode);
        var second = await GetAgentAsync(fx, "assistant");
        Assert.Equal("csv-summary", Assert.Single(second.Skills).Name);
        Assert.Equal(fx.Time.GetUtcNow().UtcDateTime, second.SkillsReportedAt);

        // The list endpoint carries the same fields.
        using var admin = fx.Admin();
        var list = await admin.GetFromJsonAsync<AgentListResponse>("/api/agents", SharpClawJson.Options);
        Assert.Equal("csv-summary", Assert.Single(Assert.Single(list!.Agents).Skills).Name);
    }

    [Fact]
    public async Task An_empty_list_clears_the_skills_but_records_the_report()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (_, token) = await fx.CreateAgentAsync();
        using var agent = fx.AgentClient(token);
        await ReportAsync(agent, Skill("daily-notes"));

        Assert.Equal(HttpStatusCode.NoContent, (await ReportAsync(agent)).StatusCode);

        var dto = await GetAgentAsync(fx, "assistant");
        Assert.Empty(dto.Skills);
        Assert.NotNull(dto.SkillsReportedAt);
    }

    [Fact]
    public async Task Another_agents_skills_are_never_touched()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (_, tokenA) = await fx.CreateAgentAsync("ade");
        var (_, tokenB) = await fx.CreateAgentAsync("cody");
        using var a = fx.AgentClient(tokenA);
        using var b = fx.AgentClient(tokenB);
        await ReportAsync(a, Skill("daily-notes"));

        await ReportAsync(b, Skill("research-brief"));
        await ReportAsync(b);

        Assert.Equal("daily-notes", Assert.Single((await GetAgentAsync(fx, "ade")).Skills).Name);
        await using var db = fx.NewDb();
        Assert.Equal(1, await db.AgentSkills.CountAsync());
    }

    [Fact]
    public async Task Thread_detail_agent_includes_skills()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (created, token) = await fx.CreateAgentAsync();
        using var agent = fx.AgentClient(token);
        await ReportAsync(agent, Skill("daily-notes"));
        var thread = await fx.CreateThreadAsync(created.Id);

        using var admin = fx.Admin();
        var detail = await admin.GetFromJsonAsync<ThreadDetailResponse>($"/api/threads/{thread}", SharpClawJson.Options);

        Assert.Equal("daily-notes", Assert.Single(detail!.Agent.Skills).Name);
    }

    [Fact]
    public async Task Wrong_listener_is_404_and_no_token_is_401()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        using var client = fx.Admin();
        Assert.Equal(HttpStatusCode.NotFound, (await ReportAsync(client, Skill("daily-notes"))).StatusCode);

        using var anonymous = fx.AgentRaw();
        Assert.Equal(HttpStatusCode.Unauthorized, (await ReportAsync(anonymous, Skill("daily-notes"))).StatusCode);
    }

    public static TheoryData<object> InvalidBodies() => new()
    {
        new { },
        new { skills = (object?)null },
        new { skills = new[] { Skill("Bad-Name") } },
        new { skills = new[] { Skill("double--hyphen") } },
        new { skills = new[] { Skill(new string('a', 65)) } },
        new { skills = new[] { Skill("x", "") } },
        new { skills = new[] { Skill("x", "   ") } },
        new { skills = new[] { Skill("x", new string('d', 1025)) } },
        new { skills = new[] { Skill("x", "two\nlines") } },
        new { skills = new[] { Skill("x"), Skill("x") } },
        new { skills = Enumerable.Range(0, 101).Select(i => Skill($"s{i}")).ToArray() },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Invalid_reports_are_400_and_change_nothing(object body)
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (_, token) = await fx.CreateAgentAsync();
        using var agent = fx.AgentClient(token);
        await ReportAsync(agent, Skill("daily-notes"));

        var resp = await agent.PutAsJsonAsync("/agent/skills", body);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(ErrorCodes.ValidationFailed, await ClientApiHelpers.ErrorCodeAsync(resp));
        Assert.Equal("daily-notes", Assert.Single((await GetAgentAsync(fx, "assistant")).Skills).Name);
    }

    [Fact]
    public async Task Exactly_100_skills_and_1024_character_descriptions_are_accepted()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (_, token) = await fx.CreateAgentAsync();
        using var agent = fx.AgentClient(token);
        var skills = Enumerable.Range(0, 100).Select(i => Skill($"s{i:000}", new string('d', 1024))).ToArray();

        Assert.Equal(HttpStatusCode.NoContent, (await ReportAsync(agent, skills)).StatusCode);
        Assert.Equal(100, (await GetAgentAsync(fx, "assistant")).Skills.Count);
    }
}
