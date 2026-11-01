using System.Net;
using SharpClaw.Contracts;

namespace SharpClaw.Gateway.Tests;

public partial class AttachmentTests
{
    [Fact]
    public async Task An_image_downloads_inline_with_its_type_and_nosniff()
    {
        var (fx, thread, _) = await SetupAsync();
        await using var _fx = fx;
        await PostAsync(fx, thread, "x", Upload("shot.png", Png));
        var id = Assert.Single((await LastUserMessageAsync(fx, thread)).Attachments!).Id;

        using var admin = fx.Admin();
        var resp = await admin.GetAsync($"/api/threads/{thread}/attachments/{id}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(Png, await resp.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", resp.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", Assert.Single(resp.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("inline", resp.Content.Headers.ContentDisposition!.DispositionType);
        Assert.True(resp.Headers.CacheControl!.NoStore); // like every /api response
    }

    [Fact]
    public async Task A_text_file_downloads_as_an_attachment_with_its_name()
    {
        var (fx, thread, _) = await SetupAsync();
        await using var _fx = fx;
        await PostAsync(fx, thread, "x", TextUpload("my notes.md", "# héllo"));
        var id = Assert.Single((await LastUserMessageAsync(fx, thread)).Attachments!).Id;

        using var admin = fx.Admin();
        var resp = await admin.GetAsync($"/api/threads/{thread}/attachments/{id}");

        Assert.Equal("# héllo", await resp.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", resp.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", resp.Content.Headers.ContentType.CharSet);
        var disposition = resp.Content.Headers.ContentDisposition!;
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.Contains("my notes.md", disposition.FileName ?? disposition.FileNameStar);
    }

    [Fact]
    public async Task An_attachment_is_404_from_another_thread_or_unknown()
    {
        var (fx, thread, _) = await SetupAsync();
        await using var _fx = fx;
        await PostAsync(fx, thread, "x", Upload("shot.png", Png));
        var id = Assert.Single((await LastUserMessageAsync(fx, thread)).Attachments!).Id;
        var agent = (await fx.CreateAgentAsync("other")).Agent;
        var other = await fx.CreateThreadAsync(agent.Id);

        using var admin = fx.Admin();
        var wrongThread = await admin.GetAsync($"/api/threads/{other}/attachments/{id}");
        var unknown = await admin.GetAsync($"/api/threads/{thread}/attachments/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, wrongThread.StatusCode);
        Assert.Equal(ErrorCodes.NotFound, await ClientApiHelpers.ErrorCodeAsync(wrongThread));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Downloads_need_client_auth_and_the_client_listener()
    {
        var (fx, thread, token) = await SetupAsync();
        await using var _fx = fx;
        await PostAsync(fx, thread, "x", Upload("shot.png", Png));
        var id = Assert.Single((await LastUserMessageAsync(fx, thread)).Attachments!).Id;
        var path = $"/api/threads/{thread}/attachments/{id}";

        using var anonymous = fx.ClientRaw();
        using var agent = fx.AgentClient(token);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync(path)).StatusCode);
    }
}
