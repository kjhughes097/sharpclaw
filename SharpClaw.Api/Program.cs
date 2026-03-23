using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using SharpClaw.Core;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// ── Configuration ────────────────────────────────────────────────────────────

var personasDir = app.Configuration["PersonasDir"]
    ?? Path.Combine(AppContext.BaseDirectory, "personas");

var expectedApiKey = app.Configuration["ApiKey"]
    ?? Environment.GetEnvironmentVariable("SHARPCLAW_API_KEY");

// ── Session store (SQLite) ───────────────────────────────────────────────────

var dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "sharpclaw", "sessions.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
var store = new SessionStore(dbPath);

// ── Live agent runners keyed by session ID ───────────────────────────────────

var runners = new ConcurrentDictionary<string, AgentRunner>();

// ── In-flight message streams keyed by "sessionId/msgId" ─────────────────────

var messageStreams = new ConcurrentDictionary<string, Channel<AgentEvent>>();

// ── API key middleware ────────────────────────────────────────────────────────

app.Use(async (context, next) =>
{
    // Skip auth for health check.
    if (context.Request.Path.Equals("/", StringComparison.Ordinal))
    {
        await next();
        return;
    }

    if (string.IsNullOrEmpty(expectedApiKey))
    {
        await next();
        return;
    }

    if (!context.Request.Headers.TryGetValue("X-Api-Key", out var providedKey) ||
        !string.Equals(providedKey, expectedApiKey, StringComparison.Ordinal))
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid API key." });
        return;
    }

    await next();
});

// ── JSON serializer options ──────────────────────────────────────────────────

var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

// ── Routes ───────────────────────────────────────────────────────────────────

app.MapGet("/", () => Results.Ok(new { status = "ok", service = "SharpClaw" }));

// GET /personas — list available agent files.
app.MapGet("/personas", () =>
{
    if (!Directory.Exists(personasDir))
        return Results.Ok(Array.Empty<object>());

    var personas = Directory.GetFiles(personasDir, "*.agent.md")
        .Select(f =>
        {
            var persona = AgentPersonaLoader.Load(f);
            return new
            {
                file = Path.GetFileName(f),
                name = persona.Name,
                backend = persona.Backend,
                mcpServers = persona.McpServers,
            };
        })
        .ToList();

    return Results.Ok(personas);
});

// POST /sessions — create a session, return its ID.
app.MapPost("/sessions", async (CreateSessionRequest req, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Persona))
        return Results.BadRequest(new { error = "persona is required." });

    var agentFile = Path.Combine(personasDir, req.Persona);
    if (!File.Exists(agentFile))
        return Results.NotFound(new { error = $"Persona '{req.Persona}' not found." });

    var sessionId = Guid.NewGuid().ToString("N")[..12];
    var persona = AgentPersonaLoader.Load(agentFile);

    store.CreateSession(sessionId, agentFile);

    // Spin up an agent runner and cache it.
    var runner = new AgentRunner(persona);
    await runner.InitializeAsync(ct);
    runners[sessionId] = runner;

    return Results.Ok(new { sessionId, persona = persona.Name });
});

// POST /sessions/{id}/messages — non-blocking: enqueue message, return msgId.
app.MapPost("/sessions/{id}/messages", async (string id, SendMessageRequest req, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Message))
        return Results.BadRequest(new { error = "message is required." });

    // Load session.
    var conversation = store.Load(id);
    if (conversation is null)
        return Results.NotFound(new { error = $"Session '{id}' not found." });

    // Get or create runner.
    if (!runners.TryGetValue(id, out var runner))
    {
        var persona = AgentPersonaLoader.Load(conversation.AgentFile);
        runner = new AgentRunner(persona);
        await runner.InitializeAsync(ct);
        runners[id] = runner;
    }

    // Append user message.
    var userMsg = new ChatMessage(ChatRole.User, req.Message);
    conversation.AddUser(req.Message);
    store.Append(id, userMsg);

    // Create a channel for this message's events.
    var msgId = Guid.NewGuid().ToString("N")[..8];
    var streamKey = $"{id}/{msgId}";
    var channel = Channel.CreateUnbounded<AgentEvent>();
    messageStreams[streamKey] = channel;

    // Fire-and-forget: run the agent turn in the background, writing events to the channel.
    _ = Task.Run(async () =>
    {
        try
        {
            var fullText = new System.Text.StringBuilder();
            await foreach (var evt in runner.StreamAsync(
                conversation.Messages,
                eventSink: e => channel.Writer.TryWrite(e),
                ct: CancellationToken.None))
            {
                channel.Writer.TryWrite(evt);
                if (evt is DoneEvent done)
                    fullText.Append(done.Content);
            }

            // Persist assistant response.
            if (fullText.Length > 0)
            {
                var assistantMsg = new ChatMessage(ChatRole.Assistant, fullText.ToString());
                conversation.AddAssistant(fullText.ToString());
                store.Append(id, assistantMsg);
            }
        }
        catch (Exception ex)
        {
            channel.Writer.TryWrite(new DoneEvent($"Error: {ex.Message}"));
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    });

    return Results.Ok(new { sessionId = id, messageId = msgId });
});

// GET /sessions/{id}/messages/{msgId}/stream — SSE stream of AgentEvents.
app.MapGet("/sessions/{id}/messages/{msgId}/stream", async (string id, string msgId, HttpContext httpContext) =>
{
    var streamKey = $"{id}/{msgId}";
    if (!messageStreams.TryGetValue(streamKey, out var channel))
    {
        httpContext.Response.StatusCode = 404;
        await httpContext.Response.WriteAsJsonAsync(new { error = "Stream not found." });
        return;
    }

    httpContext.Response.ContentType = "text/event-stream";
    httpContext.Response.Headers.CacheControl = "no-cache";
    httpContext.Response.Headers.Connection = "keep-alive";

    var ct = httpContext.RequestAborted;
    try
    {
        await foreach (var evt in channel.Reader.ReadAllAsync(ct))
        {
            var data = JsonSerializer.Serialize(evt, evt.GetType(), jsonOpts);
            await httpContext.Response.WriteAsync($"event: {evt.Type}\ndata: {data}\n\n", ct);
            await httpContext.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { }
    finally
    {
        messageStreams.TryRemove(streamKey, out _);
    }
});

// POST /sessions/{id}/permissions/{requestId} — resolve a pending permission request.
app.MapPost("/sessions/{id}/permissions/{requestId}", (string id, string requestId, PermissionDecision decision) =>
{
    if (!runners.TryGetValue(id, out var runner))
        return Results.NotFound(new { error = $"Session '{id}' not found." });

    var gate = runner.PermissionGate;
    if (gate is null)
        return Results.StatusCode(500);

    var resolved = gate.Resolve(requestId, decision.Allow);
    if (!resolved)
        return Results.NotFound(new { error = $"No pending permission request '{requestId}'." });

    return Results.Ok(new { requestId, allowed = decision.Allow });
});

app.Run();

// ── Request DTOs ─────────────────────────────────────────────────────────────

record CreateSessionRequest(string? Persona);
record SendMessageRequest(string? Message);
record PermissionDecision(bool Allow);
