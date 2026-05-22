using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SharpClaw.Abstractions;

namespace SharpClaw.Api;

/// <summary>
/// Channel sink that delivers messages over an active WebSocket connection.
/// </summary>
internal sealed class WebSocketChannelSink(WebSocket webSocket, string channelId) : IChannelSink
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ChannelId { get; } = channelId;
    public ChannelType Type => ChannelType.Web;

    public async Task DeliverAsync(string text, CancellationToken ct = default)
    {
        if (webSocket.State != WebSocketState.Open) return;

        var payload = new FanOutMessage("fanout", text);
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await webSocket.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private sealed record FanOutMessage(string Type, string Content);
}
