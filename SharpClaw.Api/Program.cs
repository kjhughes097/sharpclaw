using System.Collections.Concurrent;
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

// POST /sessions/{id}/messages — send a message, get the full reply.
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

    // Run turn.
    var answer = await runner.SendAsync(conversation.Messages, ct: ct);

    // Append assistant message.
    var assistantMsg = new ChatMessage(ChatRole.Assistant, answer);
    conversation.AddAssistant(answer);
    store.Append(id, assistantMsg);

    return Results.Ok(new
    {
        sessionId = id,
        role = "assistant",
        content = answer,
        messageCount = conversation.Count,
    });
});

app.Run();

// ── Request DTOs ─────────────────────────────────────────────────────────────

record CreateSessionRequest(string? Persona);
record SendMessageRequest(string? Message);
