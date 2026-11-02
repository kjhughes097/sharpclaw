using System.Text.Json;
using SharpClaw.Gateway.Providers;

namespace SharpClaw.Gateway.Tests;

public partial class OpenRouterProviderTests
{
    [Fact]
    public async Task A_message_with_images_is_sent_as_text_and_image_url_parts()
    {
        var handler = FakeHandler.Ok(Sse("[DONE]"));
        var request = new ProviderChatRequest("m", new[]
        {
            new ProviderChatMessage("system", "be brief"),
            new ProviderChatMessage("user", "what is it?", Images: [new ProviderImage("image/png", [1, 2, 3])]),
            new ProviderChatMessage("user", "", Images: [new ProviderImage("image/gif", [4])]),
        });

        await foreach (var _ in Provider(handler).StreamAsync(request, default)) { }

        using var body = JsonDocument.Parse(handler.LastRequest!.Body);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal("be brief", messages[0].GetProperty("content").GetString());

        var parts = messages[1].GetProperty("content");
        Assert.Equal(2, parts.GetArrayLength());
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        Assert.Equal("what is it?", parts[0].GetProperty("text").GetString());
        Assert.Equal("image_url", parts[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,AQID", parts[1].GetProperty("image_url").GetProperty("url").GetString());

        // No text: just the image part.
        var only = Assert.Single(messages[2].GetProperty("content").EnumerateArray());
        Assert.Equal("data:image/gif;base64,BA==", only.GetProperty("image_url").GetProperty("url").GetString());
    }
}
