using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Tests;

/// <summary>Files sent with a message (attachments spec).</summary>
public partial class AttachmentTests
{
    // Minimal byte sequences carrying each format's magic number; the gateway only sniffs the header.
    internal static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    internal static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10];
    internal static readonly byte[] Gif = "GIF89a\u0001\u0000"u8.ToArray();
    internal static readonly byte[] Webp = "RIFF\u0004\u0000\u0000\u0000WEBPVP8 "u8.ToArray();

    internal static AttachmentUpload Upload(string name, byte[] data) => new(name, Convert.ToBase64String(data));
    internal static AttachmentUpload TextUpload(string name, string text) => Upload(name, System.Text.Encoding.UTF8.GetBytes(text));

    private static async Task<(GatewayFixture Fx, Guid Thread, string Token)> SetupAsync(
        IDictionary<string, string?>? config = null)
    {
        var fx = await GatewayFixture.CreateAsync(extraConfig: config);
        var (agent, token) = await fx.CreateAgentAsync("assistant");
        return (fx, await fx.CreateThreadAsync(agent.Id), token);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        GatewayFixture fx, Guid thread, string content, params AttachmentUpload[] attachments)
    {
        using var admin = fx.Admin();
        return await admin.PostAsJsonAsync($"/api/threads/{thread}/messages",
            new PostMessageRequest(content, Attachments: attachments), SharpClawJson.Options);
    }

    private static async Task<MessageDto> LastUserMessageAsync(GatewayFixture fx, Guid thread)
    {
        using var admin = fx.Admin();
        var list = await admin.GetFromJsonAsync<MessageListResponse>($"/api/threads/{thread}/messages", SharpClawJson.Options);
        return list!.Messages.Last(m => m.Role == Roles.User);
    }

    [Fact]
    public async Task Attachments_are_stored_with_the_message_and_listed_without_their_bytes()
    {
        var (fx, thread, _) = await SetupAsync();
        await using var _fx = fx;

        var resp = await PostAsync(fx, thread, "look at these",
            Upload("shot.png", Png), TextUpload("notes.md", "# hi"));
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var msg = await LastUserMessageAsync(fx, thread);
        Assert.Equal("look at these", msg.Content);
        Assert.NotNull(msg.Attachments);
        Assert.Collection(msg.Attachments,
            a => Assert.Equal(("shot.png", AttachmentKinds.Image, "image/png", (long)Png.Length),
                (a.FileName, a.Kind, a.MediaType, a.SizeBytes)),
            a => Assert.Equal(("notes.md", AttachmentKinds.Text, AttachmentRules.TextMediaType, 4L),
                (a.FileName, a.Kind, a.MediaType, a.SizeBytes)));
    }

    [Fact]
    public async Task Messages_without_attachments_have_null_attachments()
    {
        var (fx, thread, _) = await SetupAsync();
        await using var _fx = fx;
        await fx.PostUserMessageAsync(thread, "plain");

        Assert.Null((await LastUserMessageAsync(fx, thread)).Attachments);
    }

    [Fact]
    public async Task Every_image_format_is_accepted_when_its_bytes_match()
    {
        var (fx, thread, _) = await SetupAsync();
        await using var _fx = fx;

        var resp = await PostAsync(fx, thread, "x",
            Upload("a.png", Png), Upload("b.JPG", Jpeg), Upload("c.jpeg", Jpeg), Upload("d.gif", Gif), Upload("e.webp", Webp));
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var msg = await LastUserMessageAsync(fx, thread);
        Assert.Equal(["image/png", "image/jpeg", "image/jpeg", "image/gif", "image/webp"],
            msg.Attachments!.Select(a => a.MediaType));
    }

    [Fact]
    public async Task An_attachment_only_message_is_accepted_and_previews_its_file_names()
    {
        var fx = await GatewayFixture.CreateAsync();
        await using var _fx = fx;
        var (agent, _) = await fx.CreateAgentAsync("assistant");
        using var admin = fx.Admin();
        var created = await (await admin.PostAsJsonAsync("/api/threads",
            new CreateThreadRequest(agent.Id.ToString(), null), SharpClawJson.Options))
            .Content.ReadFromJsonAsync<CreatedThreadResponse>(SharpClawJson.Options);
        var thread = created!.Thread.Id;

        var resp = await PostAsync(fx, thread, "", Upload("shot.png", Png), TextUpload("a.txt", "x"));
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        using var scope = fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var t = await db.Threads.AsNoTracking().SingleAsync(x => x.Id == thread);
        Assert.Equal("Attached: shot.png, a.txt", t.LastPreview);
        Assert.Equal("Attached: shot.png, a.txt", t.Title);
    }

    [Fact]
    public async Task Empty_content_without_attachments_is_still_rejected()
    {
        var (fx, thread, _) = await SetupAsync();
        await using var _fx = fx;
        var resp = await PostAsync(fx, thread, "");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(ErrorCodes.ValidationFailed, await ClientApiHelpers.ErrorCodeAsync(resp));
    }

    public static TheoryData<string, string> Rejections => new()
    {
        { "bad.exe", "not a supported file type" },
        { "fake.png", "not a valid PNG image" },
        { "photo.jpg", "not a valid JPEG image" },
        { "binary.txt", "not UTF-8 text" },
        { "nul.txt", "not UTF-8 text" },
        { "notbase64.md", "must be base64" },
        { "empty.md", "is empty" },
        { "image.svg", "not a supported file type" },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task Invalid_attachments_are_rejected_naming_the_file(string name, string expected)
    {
        var (fx, thread, _) = await SetupAsync();
        await using var _fx = fx;

        var upload = name switch
        {
            "fake.png" => Upload(name, Jpeg), // a JPEG renamed to .png
            "photo.jpg" => TextUpload(name, "not an image"),
            "binary.txt" => Upload(name, [0xC3, 0x28]), // invalid UTF-8
            "nul.txt" => Upload(name, [(byte)'a', 0, (byte)'b']),
            "notbase64.md" => new AttachmentUpload(name, "%%%"),
            "empty.md" => new AttachmentUpload(name, ""),
            "image.svg" => TextUpload(name, "<svg/>"),
            _ => Upload(name, [1, 2, 3]),
        };
        var resp = await PostAsync(fx, thread, "x", upload);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var error = (await resp.Content.ReadFromJsonAsync<ApiErrorResponse>(SharpClawJson.Options))!.Error;
        Assert.Equal(ErrorCodes.ValidationFailed, error.Code);
        Assert.Contains(expected, error.Message);
        Assert.Contains($"'{name}'", error.Message);
        await AssertNothingStoredAsync(fx);
    }

    [Fact]
    public async Task Size_and_count_limits_are_enforced()
    {
        var (fx, thread, _) = await SetupAsync(new Dictionary<string, string?>
        {
            ["Messages:MaxAttachments"] = "2",
            ["Messages:MaxImageBytes"] = "16",
            ["Messages:MaxTextBytes"] = "8",
        });
        await using var _fx = fx;

        var tooMany = await PostAsync(fx, thread, "x", TextUpload("a.txt", "a"), TextUpload("b.txt", "b"), TextUpload("c.txt", "c"));
        Assert.Equal("at most 2 attachments per message.", await MessageOf(tooMany));

        var bigImage = await PostAsync(fx, thread, "x", Upload("big.png", [.. Png, .. new byte[5]]));
        Assert.Equal("'big.png' is larger than 16 bytes.", await MessageOf(bigImage));

        var bigText = await PostAsync(fx, thread, "x", TextUpload("big.txt", "123456789"));
        Assert.Equal("'big.txt' is larger than 8 bytes.", await MessageOf(bigText));

        var ok = await PostAsync(fx, thread, "x", Upload("ok.png", Png), TextUpload("ok.txt", "12345678"));
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
    }

    [Fact]
    public async Task File_names_are_reduced_to_a_clean_last_segment()
    {
        var (fx, thread, _) = await SetupAsync();
        await using var _fx = fx;
        await PostAsync(fx, thread, "x", TextUpload("../../etc/sub\\dir/no\ttes\u0001.md", "x"));
        var msg = await LastUserMessageAsync(fx, thread);
        Assert.Equal("notes.md", Assert.Single(msg.Attachments!).FileName);
    }

    [Fact]
    public async Task The_largest_valid_request_fits_the_raised_body_limit()
    {
        // Five 5 MB images base64-encode to ~33 MB, above Kestrel's 30 MB default.
        var (fx, thread, _) = await SetupAsync();
        await using var _fx = fx;
        var image = new byte[AttachmentRules.DefaultMaxImageBytes];
        Png.CopyTo(image, 0);
        var uploads = Enumerable.Range(1, AttachmentRules.DefaultMaxAttachments).Select(i => Upload($"{i}.png", image)).ToArray();

        var resp = await PostAsync(fx, thread, "big", uploads);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    private static async Task<string> MessageOf(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<ApiErrorResponse>(SharpClawJson.Options))!.Error.Message;

    private static async Task AssertNothingStoredAsync(GatewayFixture fx)
    {
        using var scope = fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        Assert.False(await db.MessageAttachments.AnyAsync());
        Assert.False(await db.Messages.AnyAsync());
    }
}
