using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Tests;

/// <summary>Per-thread model override (3.3.1 spec section 3.3).</summary>
public class ModelOverrideTests
{
    private const string AgentModel = "test/model";
    private const string Alt = "alt/model";

    private static Task<GatewayFixture> StartAsync(params string[] allowed) =>
        GatewayFixture.CreateAsync(extraConfig: allowed
            .Select((m, i) => KeyValuePair.Create($"Models:Allowed:{i}", (string?)m))
            .ToDictionary());

    [Fact]
    public void Resolve_uses_an_allowed_override_and_falls_back_otherwise()
    {
        Assert.Equal((AgentModel, false), ModelOverride.Resolve(null, AgentModel, [Alt]));
        Assert.Equal((Alt, false), ModelOverride.Resolve(Alt, AgentModel, [Alt]));
        Assert.Equal((AgentModel, true), ModelOverride.Resolve("gone/model", AgentModel, [Alt]));
    }

    [Fact]
    public async Task System_lists_the_allowed_models()
    {
        await using var fx = await StartAsync(Alt, "other/model");
        using var admin = fx.Admin();
        var info = (await admin.GetFromJsonAsync<SystemInfoResponse>("/api/system", SharpClawJson.Options))!;
        Assert.Equal([Alt, "other/model"], info.Models);
    }

    [Fact]
    public async Task Create_and_patch_validate_the_model_and_publish_it()
    {
        await using var fx = await StartAsync(Alt);
        await fx.CreateAgentAsync(model: AgentModel);
        using var admin = fx.Admin();

        var bad = await admin.PostAsJsonAsync("/api/threads", new CreateThreadRequest("assistant", null, "nope/model"), SharpClawJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var created = await admin.PostAsJsonAsync("/api/threads", new CreateThreadRequest("assistant", null, Alt), SharpClawJson.Options);
        var thread = (await created.Content.ReadFromJsonAsync<CreatedThreadResponse>(SharpClawJson.Options))!.Thread;
        Assert.Equal(Alt, thread.Model);
        var list = (await admin.GetFromJsonAsync<ThreadListResponse>("/api/threads", SharpClawJson.Options))!;
        Assert.Equal(Alt, Assert.Single(list.Threads).Model);

        var (client, stream) = await fx.OpenStreamAsync("/api/events");
        using var _ = client;
        var read = TestHelpers.ReadSseEventsAsync(stream, 1, TimeSpan.FromSeconds(5));

        var cleared = await admin.PatchAsJsonAsync($"/api/threads/{thread.Id}", new UpdateThreadRequest(null, null, ""), SharpClawJson.Options);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null((await cleared.Content.ReadFromJsonAsync<ThreadResponse>(SharpClawJson.Options))!.Thread.Model);
        var evt = Assert.Single(await read);
        Assert.Equal(new ThreadUpdatedEvent(thread.Id, null, false, null), ClientApiHelpers.Read<ThreadUpdatedEvent>(evt.Data));

        var disallowed = await admin.PatchAsJsonAsync($"/api/threads/{thread.Id}", new UpdateThreadRequest(null, null, "nope/model"), SharpClawJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, disallowed.StatusCode);
        Assert.Equal(ErrorCodes.ValidationFailed, await ClientApiHelpers.ErrorCodeAsync(disallowed));
    }

    [Fact]
    public async Task With_no_allow_list_any_override_is_rejected()
    {
        await using var fx = await StartAsync();
        var (agent, _) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        using var admin = fx.Admin();
        var resp = await admin.PatchAsJsonAsync($"/api/threads/{thread}", new UpdateThreadRequest(null, null, Alt), SharpClawJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Completions_use_the_override_and_fall_back_when_it_is_no_longer_allowed()
    {
        await using var fx = await StartAsync(Alt);
        var (agent, token) = await fx.CreateAgentAsync(model: AgentModel);
        var thread = await fx.CreateThreadAsync(agent.Id);
        using var admin = fx.Admin();
        (await admin.PatchAsJsonAsync($"/api/threads/{thread}", new UpdateThreadRequest(null, null, Alt), SharpClawJson.Options))
            .EnsureSuccessStatusCode();

        var first = await fx.PostUserMessageAsync(thread, "one");
        await fx.ClaimAsync(token);
        (await fx.ChatAsync(token, first, thread)).EnsureSuccessStatusCode();

        // Simulate the id being removed from Models:Allowed after it was chosen.
        await using (var db = fx.NewDb())
            await db.Threads.Where(t => t.Id == thread).ExecuteUpdateAsync(s => s.SetProperty(t => t.Model, "gone/model"));
        (await fx.ChatAsync(token, first, thread)).EnsureSuccessStatusCode();

        await using var check = fx.NewDb();
        var models = await check.LlmCalls.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Select(c => c.Model).ToListAsync();
        Assert.Equal([Alt, AgentModel], models);
    }
}
