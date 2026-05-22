using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SharpClaw.Interactions;
using SharpClaw.Models;
using SharpClaw.Scheduling;
using SharpClaw.Sessions;

namespace SharpClaw.Api;

internal static class ChatWebSocketEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapChatWebSocketEndpoints(this IEndpointRouteBuilder app)
    {
        app.Map("/ws/chat/{agentName}", async (
            HttpContext context,
            string agentName,
            AgentSessionRegistry sessionRegistry,
            AgentInvoker invoker) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("ChatWebSocket");

            logger.LogInformation("WebSocket connected for agent {AgentName}", agentName);

            var channelKey = $"web:{agentName}";
            var session = sessionRegistry.GetOrCreate(channelKey, agentName);

            try
            {
                await HandleConnectionAsync(ws, session, agentName, channelKey, invoker, logger, context.RequestAborted);
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                logger.LogDebug("WebSocket closed prematurely for agent {AgentName}", agentName);
            }
            catch (OperationCanceledException)
            {
                // Client disconnected
            }
            finally
            {
                logger.LogInformation("WebSocket disconnected for agent {AgentName}", agentName);
            }
        });
    }

    private static async Task HandleConnectionAsync(
        WebSocket ws,
        AgentSession session,
        string agentName,
        string channelKey,
        AgentInvoker invoker,
        ILogger logger,
        CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var message = await ReceiveFullMessageAsync(ws, buffer, ct);
            if (message is null)
                break;

            var inbound = JsonSerializer.Deserialize<WsInboundMessage>(message, JsonOptions);
            if (inbound is null || string.IsNullOrWhiteSpace(inbound.Text))
                continue;

            // Publish inbound message (same as HTTP/Telegram path)
            await session.PublishAsync(new AgentMessage(
                session.SessionId,
                Guid.NewGuid().ToString(),
                MessageOrigin.Web,
                agentName,
                inbound.Text,
                DateTimeOffset.UtcNow), ct);

            // Send typing indicator
            await SendMessageAsync(ws, new WsOutboundMessage("typing", null, null), ct);

            // Invoke agent (long-running — this is the whole point of WebSocket)
            try
            {
                var schedulingCtx = new SchedulingContext(channelKey, ScheduleChannelType.Web, agentName);
                var (switchedTo, responseText) = await invoker.InvokeAsync(session, inbound.Text, schedulingCtx, ct);

                await SendMessageAsync(ws, new WsOutboundMessage("response", responseText, switchedTo), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Agent invocation failed for {AgentName}", agentName);
                await SendMessageAsync(ws, new WsOutboundMessage("error", $"Agent error: {ex.Message}", null), ct);
            }
        }
    }

    private static async Task<string?> ReceiveFullMessageAsync(
        WebSocket ws, byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task SendMessageAsync(WebSocket ws, WsOutboundMessage msg, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;

        var json = JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
        await ws.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private sealed record WsInboundMessage(string Text);
    private sealed record WsOutboundMessage(string Type, string? Content, string? SwitchedTo);
}
