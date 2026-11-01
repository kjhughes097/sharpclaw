using System.Net;
using System.Net.Http.Json;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Tests;

/// <summary>Attachments in the model context and on the chat completion proxy (attachments spec sections 4–5).</summary>
public partial class AttachmentTests
{
    // ---- unit: replay ----

    [Fact]
    public void Text_files_are_inlined_after_the_text_in_a_fence()
    {
        var m = ModelReplay.WithAttachments("summarise",
            [new ReplayAttachment(Guid.NewGuid(), AttachmentKinds.Text, "notes.md", "# hi\n")]);

        Assert.Equal("summarise\n\n[Attached file: notes.md]\n```\n# hi\n```", m.Content);
        Assert.Null(m.ImageIds);
    }

    [Fact]
    public void The_fence_is_longer_than_any_backtick_run_in_the_file()
    {
        var m = ModelReplay.WithAttachments("",
            [new ReplayAttachment(Guid.NewGuid(), AttachmentKinds.Text, "a.md", "```cs\nx\n````")]);

        Assert.Equal("[Attached file: a.md]\n`````\n```cs\nx\n````\n`````", m.Content);
    }

    [Fact]
    public void Images_get_a_marker_line_and_ride_along_by_id()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var m = ModelReplay.WithAttachments("what's this?",
        [
            new ReplayAttachment(a, AttachmentKinds.Image, "one.png", null),
            new ReplayAttachment(Guid.NewGuid(), AttachmentKinds.Text, "t.txt", "x"),
            new ReplayAttachment(b, AttachmentKinds.Image, "two.jpg", null),
        ]);

        Assert.Equal(Roles.User, m.Role);
        Assert.Equal(
            "what's this?\n\n[Attached image: one.png]\n\n[Attached file: t.txt]\n```\nx\n```\n\n[Attached image: two.jpg]",
            m.Content);
        Assert.Equal([a, b], m.ImageIds);
    }

    [Fact]
    public void Each_image_adds_the_configured_tokens_to_the_estimate()
    {
        var options = new ContextOptions { ImageTokens = 500 };
        var estimator = new TokenEstimator(options);
        var plain = estimator.Estimate(new ContextMessage(Roles.User, "abcd"));
        var withImages = estimator.Estimate(new ContextMessage(Roles.User, "abcd", ImageIds: [Guid.NewGuid(), Guid.NewGuid()]));
        Assert.Equal(plain + 1000, withImages);
    }

    // ---- integration: context and /v1 ----

    [Fact]
    public async Task The_context_inlines_text_and_lists_image_ids_on_every_later_turn()
    {
        var (fx, thread, token) = await SetupAsync();
        await using var _fx = fx;
        await PostAsync(fx, thread, "first", Upload("shot.png", Png), TextUpload("a.txt", "file body"));
        var first = await LastUserMessageAsync(fx, thread);
        var imageId = first.Attachments![0].Id;
        await fx.ReplyAsync(token, thread, first.Id, "ok");
        await fx.PostUserMessageAsync(thread, "second");
        await fx.ClaimAsync(token);

        using var agent = fx.AgentClient(token);
        var context = await agent.GetFromJsonAsync<ContextResponse>(
            $"/agent/threads/{thread}/context?reserveTokens=0", SharpClawJson.Options);

        var user = context!.Messages[0];
        Assert.Equal("first\n\n[Attached image: shot.png]\n\n[Attached file: a.txt]\n```\nfile body\n```", user.Content);
        Assert.Equal([imageId], user.ImageIds);
        Assert.Null(context.Messages[^1].ImageIds); // "second" has none
        Assert.True(context.EstimatedTokens > new ContextOptions().ImageTokens);
    }

    [Fact]
    public async Task Image_ids_reach_the_provider_as_image_bytes()
    {
        var (fx, thread, token) = await SetupAsync();
        await using var _fx = fx;
        await PostAsync(fx, thread, "look", Upload("shot.png", Png));
        var imageId = (await LastUserMessageAsync(fx, thread)).Attachments![0].Id;
        var claimed = (await fx.ClaimAsync(token))!;

        var resp = await fx.ChatRawAsync(token, claimed.Id, thread, ChatWithImages("look", imageId));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var (content, _, _, _, _) = ChatTestHelpers.ParseChat(await resp.Content.ReadAsStringAsync());
        Assert.Equal($"Fake reply to: look [images: image/png {Png.Length} bytes]", content);
    }

    [Fact]
    public async Task Image_ids_must_be_images_attached_in_the_requests_thread()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync("assistant");
        var thread = await fx.CreateThreadAsync(agent.Id);
        // An image in another of the agent's threads, and a text attachment in this one.
        var other = await fx.CreateThreadAsync(agent.Id);
        await PostAsync(fx, other, "elsewhere", Upload("other.png", Png));
        var otherMessage = await LastUserMessageAsync(fx, other);
        var otherImage = otherMessage.Attachments![0].Id;
        await fx.ReplyAsync(token, other, otherMessage.Id, "ok");

        await PostAsync(fx, thread, "here", TextUpload("a.txt", "x"));
        var textId = (await LastUserMessageAsync(fx, thread)).Attachments![0].Id;
        var claimed = (await fx.ClaimAsync(token))!;

        foreach (var id in new[] { otherImage, textId, Guid.NewGuid() })
        {
            var resp = await fx.ChatRawAsync(token, claimed.Id, thread, ChatWithImages("x", id));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Equal(ErrorCodes.ValidationFailed, await ClientApiHelpers.ErrorCodeAsync(resp));
        }
    }

    [Theory]
    [InlineData("assistant", """["00000000-0000-0000-0000-000000000001"]""")]
    [InlineData("user", "\"not-an-array\"")]
    [InlineData("user", """["nope"]""")]
    public async Task Malformed_image_ids_are_rejected(string role, string imageIds)
    {
        var (fx, thread, token) = await SetupAsync();
        await using var _fx = fx;
        await fx.PostUserMessageAsync(thread, "x");
        var claimed = (await fx.ClaimAsync(token))!;

        var payload = System.Text.Json.Nodes.JsonNode.Parse($$"""
            { "stream": true, "messages": [ { "role": "{{role}}", "content": "x", "image_ids": {{imageIds}} } ] }
            """)!;
        var resp = await fx.ChatRawAsync(token, claimed.Id, thread, payload);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(ErrorCodes.ValidationFailed, await ClientApiHelpers.ErrorCodeAsync(resp));
    }

    private static object ChatWithImages(string text, Guid imageId) => new Dictionary<string, object?>
    {
        ["stream"] = true,
        ["messages"] = new object[] { new Dictionary<string, object?> { ["role"] = "user", ["content"] = text, ["image_ids"] = new[] { imageId } } },
    };
}
