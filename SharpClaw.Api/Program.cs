using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.FileProviders;
using SharpClaw.Copilot;
using SharpClaw.Core;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// ── Configuration ────────────────────────────────────────────────────────────

var expectedApiKey = app.Configuration["ApiKey"]
    ?? Environment.GetEnvironmentVariable("SHARPCLAW_API_KEY");

// ── Session store (PostgreSQL) ───────────────────────────────────────────────

var connectionString = app.Configuration.GetConnectionString("DefaultConnection")
    ?? Environment.GetEnvironmentVariable("SHARPCLAW_DB_CONNECTION")
    ?? "Host=localhost;Database=sharpclaw;Username=sharpclaw;Password=sharpclaw";

var store = new SessionStore(connectionString);

// ── Live agent runners keyed by session ID ───────────────────────────────────

var runners = new ConcurrentDictionary<string, AgentRunner>();

// ── In-flight message streams keyed by "sessionId/msgId" ─────────────────────

var messageStreams = new ConcurrentDictionary<string, Channel<AgentEvent>>();

// ── Backend factory (resolves "anthropic" and "copilot") ─────────────────────

IAgentBackend CreateBackend(AgentPersona persona, PermissionGate gate) => persona.Backend switch
{
    "anthropic" => new AnthropicBackend(new AnthropicClient(new ClientOptions
    {
        ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? throw new InvalidOperationException("ANTHROPIC_API_KEY is not set."),
    }), string.IsNullOrWhiteSpace(persona.Model) ? "claude-haiku-4-5-20251001" : persona.Model),
    "copilot" => new CopilotBackend(gate,
        Environment.GetEnvironmentVariable("SHARPCLAW_WORKSPACE") ?? Environment.CurrentDirectory),
    _ => throw new InvalidOperationException($"Unknown backend '{persona.Backend}'."),
};

// ── API key middleware (only applies to /api/* routes) ────────────────────────

app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api"))
    {
        await next();
        return;
    }

    // SSE stream uses EventSource which can't set custom headers — skip auth.
    // Security: requires knowing a valid sessionId + messageId (short-lived).
    if (context.Request.Path.Value?.EndsWith("/stream") == true &&
        context.Request.Method == "GET")
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

// ── Static files (SPA) ──────────────────────────────────────────────────────

// Look in content root first (dev), then publish dir (production).
var wwwroot = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
if (!Directory.Exists(wwwroot))
    wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (Directory.Exists(wwwroot))
{
    var fileProvider = new PhysicalFileProvider(wwwroot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
}

// ── JSON serializer options ──────────────────────────────────────────────────

var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

object ToPersonaDto(AgentRecord agent) => new
{
    file = agent.Filename,
    name = agent.Name,
    description = agent.Description,
    backend = agent.Backend,
    model = agent.Model,
    mcpServers = agent.McpServers,
    permissionPolicy = agent.PermissionPolicy,
    systemPrompt = agent.SystemPrompt,
    isEnabled = agent.IsEnabled,
};

object ToAgentDto(AgentRecord agent, int sessionCount) => new
{
    file = agent.Filename,
    name = agent.Name,
    description = agent.Description,
    backend = agent.Backend,
    model = agent.Model,
    mcpServers = agent.McpServers,
    permissionPolicy = agent.PermissionPolicy,
    systemPrompt = agent.SystemPrompt,
    isEnabled = agent.IsEnabled,
    sessionCount,
};

IResult? ValidateAgentRequest(AgentDefinitionRequest req, bool creating)
{
    if (creating && string.IsNullOrWhiteSpace(req.File))
        return Results.BadRequest(new { error = "file is required." });
    if (string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest(new { error = "name is required." });
    if (string.IsNullOrWhiteSpace(req.Description))
        return Results.BadRequest(new { error = "description is required." });
    if (string.IsNullOrWhiteSpace(req.Backend))
        return Results.BadRequest(new { error = "backend is required." });
    if (string.IsNullOrWhiteSpace(req.SystemPrompt))
        return Results.BadRequest(new { error = "systemPrompt is required." });

    var backend = req.Backend.Trim().ToLowerInvariant();
    if (backend is not ("anthropic" or "copilot"))
        return Results.BadRequest(new { error = "backend must be either 'anthropic' or 'copilot'." });

    return null;
}

AgentRecord ToAgentRecord(AgentDefinitionRequest req, string? filenameOverride = null) => new(
    Filename: filenameOverride ?? req.File!.Trim(),
    Name: req.Name!.Trim(),
    Description: req.Description!.Trim(),
    Backend: req.Backend!.Trim().ToLowerInvariant(),
    Model: req.Model?.Trim() ?? string.Empty,
    McpServers: (req.McpServers ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
    PermissionPolicy: (req.PermissionPolicy ?? new Dictionary<string, string>())
        .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
        .ToDictionary(kvp => kvp.Key.Trim(), kvp => kvp.Value.Trim(), StringComparer.OrdinalIgnoreCase),
    SystemPrompt: req.SystemPrompt!.Trim(),
    IsEnabled: req.IsEnabled ?? true);

// ── Routes ───────────────────────────────────────────────────────────────────

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "SharpClaw" }));

// GET /api/personas — list available agent definitions from the database.
app.MapGet("/api/personas", () =>
{
    var personas = store.ListAgents(includeDisabled: false)
        .Select(ToPersonaDto)
        .ToList();

    return Results.Ok(personas);
});

app.MapGet("/api/agents", () =>
{
    var sessionCounts = store.GetSessionCountsByAgent();
    return Results.Ok(store.ListAgents().Select(agent =>
        ToAgentDto(agent, sessionCounts.GetValueOrDefault(agent.Filename, 0))).ToList());
});

app.MapPost("/api/agents", (AgentDefinitionRequest req) =>
{
    var validation = ValidateAgentRequest(req, creating: true);
    if (validation is not null)
        return validation;

    var filename = req.File!.Trim();
    if (store.GetAgent(filename) is not null)
        return Results.Conflict(new { error = $"Agent '{filename}' already exists." });

    var agent = ToAgentRecord(req);
    store.CreateAgent(agent);

    return Results.Created($"/api/agents/{Uri.EscapeDataString(agent.Filename)}", ToAgentDto(agent, 0));
});

app.MapPut("/api/agents/{filename}", (string filename, AgentDefinitionRequest req) =>
{
    var validation = ValidateAgentRequest(req, creating: false);
    if (validation is not null)
        return validation;

    if (!string.IsNullOrWhiteSpace(req.File) &&
        !string.Equals(req.File.Trim(), filename, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "Renaming an existing agent file is not supported." });
    }

    var updated = ToAgentRecord(req, filename);
    return store.UpdateAgent(filename, updated)
        ? Results.Ok(ToAgentDto(updated, store.CountSessionsForAgent(filename)))
        : Results.NotFound(new { error = $"Agent '{filename}' not found." });
});

app.MapPatch("/api/agents/{filename}/enabled", (string filename, AgentEnabledRequest req) =>
{
    return store.SetAgentEnabled(filename, req.IsEnabled)
        ? Results.Ok(new { file = filename, isEnabled = req.IsEnabled })
        : Results.NotFound(new { error = $"Agent '{filename}' not found." });
});

app.MapDelete("/api/agents/{filename}", async (string filename, bool? purgeSessions) =>
{
    var agent = store.GetAgent(filename);
    if (agent is null)
        return Results.NotFound(new { error = $"Agent '{filename}' not found." });

    var linkedSessionCount = store.CountSessionsForAgent(filename);
    if (linkedSessionCount > 0 && purgeSessions != true)
    {
        return Results.Conflict(new
        {
            error = $"Agent '{filename}' has {linkedSessionCount} linked session(s). Re-run delete with purgeSessions=true to delete those sessions first.",
            linkedSessionCount,
            requiresSessionPurge = true,
        });
    }

    if (linkedSessionCount > 0)
    {
        var sessionIds = store.ListSessionIdsForAgent(filename);
        store.PurgeSessionsForAgent(filename);
        foreach (var sessionId in sessionIds)
        {
            if (runners.TryRemove(sessionId, out var runner))
                await runner.DisposeAsync();

            foreach (var streamKey in messageStreams.Keys.Where(key => key.StartsWith($"{sessionId}/", StringComparison.Ordinal)).ToList())
                messageStreams.TryRemove(streamKey, out _);
        }
    }

    return store.DeleteAgent(filename)
        ? Results.Ok(new { file = filename, deletedSessions = linkedSessionCount })
        : Results.NotFound(new { error = $"Agent '{filename}' not found." });
});

// POST /api/sessions — create a session, return its ID.
app.MapPost("/api/sessions", async (CreateSessionRequest req, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Persona))
        return Results.BadRequest(new { error = "persona is required." });

    var agentRecord = store.GetAgent(req.Persona);
    if (agentRecord is null)
        return Results.NotFound(new { error = $"Persona '{req.Persona}' not found." });
    if (!agentRecord.IsEnabled)
        return Results.Conflict(new { error = $"Persona '{req.Persona}' is disabled." });

    var sessionId = Guid.NewGuid().ToString("N")[..12];
    var persona = agentRecord.ToPersona();

    store.CreateSession(sessionId, agentRecord.Filename);

    // Spin up an agent runner and cache it.
    var runner = new AgentRunner(persona, CreateBackend);
    await runner.InitializeAsync(ct);
    runners[sessionId] = runner;

    return Results.Ok(new { sessionId, persona = persona.Name });
});

// POST /api/sessions/{id}/messages — non-blocking: enqueue message, return msgId.
app.MapPost("/api/sessions/{id}/messages", async (string id, SendMessageRequest req, CancellationToken ct) =>
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
        var agentRecord = store.GetAgent(conversation.AgentFile);
        if (agentRecord is null)
            return Results.NotFound(new { error = $"Agent definition '{conversation.AgentFile}' not found." });

        var persona = agentRecord.ToPersona();
        runner = new AgentRunner(persona, CreateBackend);
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
            // ── Coordinator routing ──────────────────────────────────────
            // If this session uses the coordinator persona, route to a specialist first.
            if (runner.Persona.Name.Equals("Coordinator", StringComparison.OrdinalIgnoreCase))
            {
                var availableAgents = new Dictionary<string, string>();
                foreach (var agentRec in store.ListAgents())
                {
                    if (agentRec.Filename.Equals("coordinator.agent.md", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!agentRec.IsEnabled)
                        continue;
                    availableAgents[agentRec.Filename] = agentRec.Name + " — " + agentRec.Description;
                }

                var coordinator = new CoordinatorAgent(
                    new AnthropicBackend(new Anthropic.AnthropicClient(
                        new Anthropic.Core.ClientOptions
                        {
                            ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")!
                        })),
                    runner.Persona);

                var decision = await coordinator.RouteAsync(req.Message, availableAgents);

                if (decision.Agent is not null)
                {
                    var specialistRecord = store.GetAgent(decision.Agent);
                    if (specialistRecord is not null)
                    {
                        var specialistPersona = specialistRecord.ToPersona();
                        var specialistRunner = new AgentRunner(specialistPersona, CreateBackend);
                        await specialistRunner.InitializeAsync(CancellationToken.None);

                        // Replace the coordinator runner with the specialist for this session.
                        await runner.DisposeAsync();
                        runner = specialistRunner;
                        runners[id] = runner;

                        // Use the rewritten prompt if available.
                        if (decision.RewrittenPrompt is not null)
                        {
                            conversation.ReplaceLastUser(decision.RewrittenPrompt);
                        }
                    }
                }
            }

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

// GET /api/sessions/{id}/messages/{msgId}/stream — SSE stream of AgentEvents.
app.MapGet("/api/sessions/{id}/messages/{msgId}/stream", async (string id, string msgId, HttpContext httpContext) =>
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

// POST /api/sessions/{id}/permissions/{requestId} — resolve a pending permission request.
app.MapPost("/api/sessions/{id}/permissions/{requestId}", (string id, string requestId, PermissionDecision decision) =>
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

// ── SPA fallback: serve index.html for non-API, non-file routes ──────────────
app.MapFallback(async context =>
{
    var indexPath = Path.Combine(wwwroot, "index.html");
    if (File.Exists(indexPath))
    {
        context.Response.ContentType = "text/html";
        await context.Response.SendFileAsync(indexPath);
    }
    else
    {
        context.Response.StatusCode = 404;
    }
});

app.Run();

// ── Request DTOs ─────────────────────────────────────────────────────────────

record CreateSessionRequest(string? Persona);
record SendMessageRequest(string? Message);
record PermissionDecision(bool Allow);
record AgentEnabledRequest(bool IsEnabled);
record AgentDefinitionRequest(
    string? File,
    string? Name,
    string? Description,
    string? Backend,
    string? Model,
    List<string>? McpServers,
    Dictionary<string, string>? PermissionPolicy,
    string? SystemPrompt,
    bool? IsEnabled);
