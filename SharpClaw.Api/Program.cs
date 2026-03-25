using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Anthropic;
using Anthropic.Core;
using GitHub.Copilot.SDK;
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
var providerHttpClient = new HttpClient();
var backendModelCache = new ConcurrentDictionary<string, (DateTimeOffset CachedAt, IReadOnlyList<(string Id, string DisplayName)> Models)>();

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
const string AdeAgentId = "ade.agent.md";

string CreateAgentId(string name)
{
    var slugChars = new List<char>();
    var previousWasDash = false;

    foreach (var ch in name.Trim().ToLowerInvariant())
    {
        if (char.IsLetterOrDigit(ch))
        {
            slugChars.Add(ch);
            previousWasDash = false;
            continue;
        }

        if (previousWasDash)
            continue;

        slugChars.Add('-');
        previousWasDash = true;
    }

    var slug = new string(slugChars.ToArray()).Trim('-');
    if (string.IsNullOrWhiteSpace(slug))
        slug = "agent";

    return $"{slug}.agent.md";
}

object ToPersonaDto(AgentRecord agent) => new
{
    id = agent.Slug,
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
    id = agent.Slug,
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

object ToMcpDto(McpServerRecord mcp, int linkedAgentCount) => new
{
    slug = mcp.Slug,
    name = mcp.Name,
    description = mcp.Description,
    command = mcp.Command,
    args = mcp.Args,
    isEnabled = mcp.IsEnabled,
    linkedAgentCount,
};

object ToBackendModelsDto(
    IReadOnlyList<(string Id, string DisplayName)> models,
    string source,
    DateTimeOffset? cachedAt = null,
    string? warning = null) => new
    {
        models = models.Select(model => new { id = model.Id, displayName = model.DisplayName }).ToList(),
        source,
        cachedAt,
        warning,
    };

object ToSessionDto(StoredSession session, Dictionary<string, AgentRecord> agentsBySlug)
{
    var conversation = store.Load(session.SessionId);
    var eventLogs = store.LoadEventLogs(session.SessionId);
    var hasAgent = agentsBySlug.TryGetValue(session.AgentSlug, out var agentRecord);
    var personaName = hasAgent ? agentRecord!.Name : session.AgentSlug;

    return new
    {
        sessionId = session.SessionId,
        persona = personaName,
        agentId = session.AgentSlug,
        createdAt = session.CreatedAt,
        lastActivityAt = session.LastActivityAt,
        messages = conversation?.Messages.Select(message => new
        {
            role = message.Role == ChatRole.User ? "user" : "assistant",
            content = message.Content,
        }).ToList() ?? [],
        eventLogs = eventLogs.Select(log => log.Select(item => new
        {
            Event = item.Event,
            result = item.Result,
        }).ToList()).ToList(),
    };
}

List<string> NormalizeStringList(IEnumerable<string>? values) => (values ?? [])
    .Where(value => !string.IsNullOrWhiteSpace(value))
    .Select(value => value.Trim())
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToList();

IResult? ValidateAgentRequest(AgentDefinitionRequest req, bool creating)
{
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

    var unknownMcp = NormalizeStringList(req.McpServers)
        .FirstOrDefault(slug => store.GetMcp(slug) is null);
    if (unknownMcp is not null)
        return Results.BadRequest(new { error = $"Unknown MCP '{unknownMcp}'." });

    return null;
}

IResult? ValidateMcpRequest(McpDefinitionRequest req, bool creating)
{
    if (creating && string.IsNullOrWhiteSpace(req.Slug))
        return Results.BadRequest(new { error = "slug is required." });
    if (string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest(new { error = "name is required." });
    if (string.IsNullOrWhiteSpace(req.Description))
        return Results.BadRequest(new { error = "description is required." });
    if (string.IsNullOrWhiteSpace(req.Command))
        return Results.BadRequest(new { error = "command is required." });

    return null;
}

AgentRecord ToAgentRecord(AgentDefinitionRequest req, string? slugOverride = null) => new(
    Slug: slugOverride ?? CreateAgentId(req.Name!.Trim()),
    Name: req.Name!.Trim(),
    Description: req.Description!.Trim(),
    Backend: req.Backend!.Trim().ToLowerInvariant(),
    Model: req.Model?.Trim() ?? string.Empty,
    McpServers: NormalizeStringList(req.McpServers),
    PermissionPolicy: (req.PermissionPolicy ?? new Dictionary<string, string>())
        .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
        .ToDictionary(kvp => kvp.Key.Trim(), kvp => kvp.Value.Trim(), StringComparer.OrdinalIgnoreCase),
    SystemPrompt: req.SystemPrompt!.Trim(),
    IsEnabled: req.IsEnabled ?? true);

McpServerRecord ToMcpRecord(McpDefinitionRequest req, string? slugOverride = null) => new(
    Slug: slugOverride ?? req.Slug!.Trim(),
    Name: req.Name!.Trim(),
    Description: req.Description!.Trim(),
    Command: req.Command!.Trim(),
    Args: NormalizeStringList(req.Args),
    IsEnabled: req.IsEnabled ?? true);

AgentRunner CreateRunner(AgentRecord agentRecord)
{
    var persona = agentRecord.ToPersona();
    var mcps = store.ListMcpsBySlug(agentRecord.McpServers, includeDisabled: false);
    return new AgentRunner(persona, mcps, CreateBackend);
}

string BuildDirectResponseSystemPrompt(AgentRecord agentRecord)
{
    return $"""
        You are {agentRecord.Name}. {agentRecord.Description}

        Help the user directly and answer in normal conversational language.
        Do not return a routing JSON object or mention internal routing behavior unless it genuinely helps the user.
        """;
}

CopilotClient CreateCopilotClient()
{
    var opts = new CopilotClientOptions
    {
        Cwd = Environment.GetEnvironmentVariable("SHARPCLAW_WORKSPACE") ?? Environment.CurrentDirectory,
    };

    var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    if (string.IsNullOrWhiteSpace(token))
        token = Environment.GetEnvironmentVariable("GITHUB_COPILOT_TOKEN");
    if (string.IsNullOrWhiteSpace(token))
        token = TryGetGhToken();

    if (!string.IsNullOrWhiteSpace(token))
        opts.GitHubToken = token;

    return new CopilotClient(opts);
}

string? TryGetGhToken()
{
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo("gh", "auth token")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null)
            return null;

        var output = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit(5000);
        return proc.ExitCode == 0 && output.StartsWith("gho_") ? output : null;
    }
    catch
    {
        return null;
    }
}

async Task<IReadOnlyList<(string Id, string DisplayName)>> ListCopilotModelsAsync(CancellationToken cancellationToken)
{
    await using var client = CreateCopilotClient();
    await client.StartAsync(cancellationToken);

    var response = await client.ListModelsAsync(cancellationToken);
    return response
        .Where(model => !string.IsNullOrWhiteSpace(model.Id))
        .Select(model => (
            model.Id,
            string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name))
        .ToList();
}

async Task<IReadOnlyList<(string Id, string DisplayName)>> ListAnthropicModelsAsync(CancellationToken cancellationToken)
{
    var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
        throw new InvalidOperationException("ANTHROPIC_API_KEY is not set.");

    using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models");
    request.Headers.Add("x-api-key", apiKey);
    request.Headers.Add("anthropic-version", "2023-06-01");

    using var response = await providerHttpClient.SendAsync(request, cancellationToken);
    var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException(
            $"Anthropic model list request failed with {(int)response.StatusCode}: {responseBody}");
    }

    using var document = JsonDocument.Parse(responseBody);
    if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        return [];

    var models = new List<(string Id, string DisplayName)>();
    foreach (var item in data.EnumerateArray())
    {
        if (!item.TryGetProperty("id", out var idElement))
            continue;

        var id = idElement.GetString();
        if (string.IsNullOrWhiteSpace(id))
            continue;

        var displayName = item.TryGetProperty("display_name", out var nameElement)
            ? nameElement.GetString()
            : null;

        models.Add((id, string.IsNullOrWhiteSpace(displayName) ? id : displayName));
    }

    return models;
}

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
        ToAgentDto(agent, sessionCounts.GetValueOrDefault(agent.Slug, 0))).ToList());
});

app.MapGet("/api/backends/{backend}/models", async (string backend, CancellationToken cancellationToken) =>
{
    var normalizedBackend = backend.Trim().ToLowerInvariant();
    if (normalizedBackend is not ("anthropic" or "copilot"))
        return Results.BadRequest(new { error = "backend must be either 'anthropic' or 'copilot'." });

    try
    {
        var models = normalizedBackend switch
        {
            "anthropic" => await ListAnthropicModelsAsync(cancellationToken),
            "copilot" => await ListCopilotModelsAsync(cancellationToken),
            _ => [],
        };

        backendModelCache[normalizedBackend] = (DateTimeOffset.UtcNow, models);

        return Results.Ok(ToBackendModelsDto(models, source: "live"));
    }
    catch (Exception ex) when (backendModelCache.TryGetValue(normalizedBackend, out var cachedModels))
    {
        return Results.Ok(ToBackendModelsDto(
            cachedModels.Models,
            source: "cache",
            cachedAt: cachedModels.CachedAt,
            warning: ex.Message));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        if (ex.Message.Contains("Not authenticated", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("authenticate first", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        return Results.Problem(
            detail: ex.Message,
            title: $"Failed to load models for backend '{normalizedBackend}'.",
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/mcps", () =>
{
    var agentCounts = store.GetAgentCountsByMcp();
    return Results.Ok(store.ListMcps().Select(mcp =>
        ToMcpDto(mcp, agentCounts.GetValueOrDefault(mcp.Slug, 0))).ToList());
});

app.MapPost("/api/agents", (AgentDefinitionRequest req) =>
{
    var validation = ValidateAgentRequest(req, creating: true);
    if (validation is not null)
        return validation;

    var agentId = CreateAgentId(req.Name!.Trim());
    if (store.GetAgent(agentId) is not null)
        return Results.Conflict(new { error = $"Agent '{req.Name!.Trim()}' already exists." });

    var agent = ToAgentRecord(req, agentId);
    store.CreateAgent(agent);

    return Results.Created($"/api/agents/{Uri.EscapeDataString(agent.Slug)}", ToAgentDto(agent, 0));
});

app.MapPut("/api/agents/{slug}", (string slug, AgentDefinitionRequest req) =>
{
    var validation = ValidateAgentRequest(req, creating: false);
    if (validation is not null)
        return validation;

    var updated = ToAgentRecord(req, slug);
    return store.UpdateAgent(slug, updated)
        ? Results.Ok(ToAgentDto(updated, store.CountSessionsForAgent(slug)))
        : Results.NotFound(new { error = $"Agent '{slug}' not found." });
});

app.MapPatch("/api/agents/{slug}/enabled", (string slug, AgentEnabledRequest req) =>
{
    return store.SetAgentEnabled(slug, req.IsEnabled)
        ? Results.Ok(new { id = slug, isEnabled = req.IsEnabled })
        : Results.NotFound(new { error = $"Agent '{slug}' not found." });
});

app.MapPost("/api/mcps", (McpDefinitionRequest req) =>
{
    var validation = ValidateMcpRequest(req, creating: true);
    if (validation is not null)
        return validation;

    var slug = req.Slug!.Trim();
    if (store.GetMcp(slug) is not null)
        return Results.Conflict(new { error = $"MCP '{slug}' already exists." });

    var mcp = ToMcpRecord(req);
    store.CreateMcp(mcp);

    return Results.Created($"/api/mcps/{Uri.EscapeDataString(mcp.Slug)}", ToMcpDto(mcp, 0));
});

app.MapPut("/api/mcps/{slug}", (string slug, McpDefinitionRequest req) =>
{
    var validation = ValidateMcpRequest(req, creating: false);
    if (validation is not null)
        return validation;

    if (!string.IsNullOrWhiteSpace(req.Slug) &&
        !string.Equals(req.Slug.Trim(), slug, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "Renaming an existing MCP slug is not supported." });
    }

    var updated = ToMcpRecord(req, slug);
    return store.UpdateMcp(slug, updated)
        ? Results.Ok(ToMcpDto(updated, store.CountAgentsForMcp(slug)))
        : Results.NotFound(new { error = $"MCP '{slug}' not found." });
});

app.MapPatch("/api/mcps/{slug}/enabled", (string slug, McpEnabledRequest req) =>
{
    return store.SetMcpEnabled(slug, req.IsEnabled)
        ? Results.Ok(new { slug, isEnabled = req.IsEnabled })
        : Results.NotFound(new { error = $"MCP '{slug}' not found." });
});

app.MapDelete("/api/mcps/{slug}", (string slug, bool? detachAgents) =>
{
    var mcp = store.GetMcp(slug);
    if (mcp is null)
        return Results.NotFound(new { error = $"MCP '{slug}' not found." });

    var linkedAgentCount = store.CountAgentsForMcp(slug);
    if (linkedAgentCount > 0 && detachAgents != true)
    {
        return Results.Conflict(new
        {
            error = $"MCP '{slug}' is linked to {linkedAgentCount} agent(s). Re-run delete with detachAgents=true to remove those references first.",
            linkedAgentCount,
            requiresAgentDetach = true,
        });
    }

    var detachedAgents = linkedAgentCount > 0 ? store.DetachMcpFromAgents(slug) : 0;

    return store.DeleteMcp(slug)
        ? Results.Ok(new { slug, detachedAgents })
        : Results.NotFound(new { error = $"MCP '{slug}' not found." });
});

app.MapDelete("/api/agents/{slug}", async (string slug, bool? purgeSessions) =>
{
    var agent = store.GetAgent(slug);
    if (agent is null)
        return Results.NotFound(new { error = $"Agent '{slug}' not found." });

    var linkedSessionCount = store.CountSessionsForAgent(slug);
    if (linkedSessionCount > 0 && purgeSessions != true)
    {
        return Results.Conflict(new
        {
            error = $"Agent '{slug}' has {linkedSessionCount} linked session(s). Re-run delete with purgeSessions=true to delete those sessions first.",
            linkedSessionCount,
            requiresSessionPurge = true,
        });
    }

    if (linkedSessionCount > 0)
    {
        var sessionIds = store.ListSessionIdsForAgent(slug);
        store.PurgeSessionsForAgent(slug);
        foreach (var sessionId in sessionIds)
        {
            if (runners.TryRemove(sessionId, out var runner))
                await runner.DisposeAsync();

            foreach (var streamKey in messageStreams.Keys.Where(key => key.StartsWith($"{sessionId}/", StringComparison.Ordinal)).ToList())
                messageStreams.TryRemove(streamKey, out _);
        }
    }

    return store.DeleteAgent(slug)
        ? Results.Ok(new { id = slug, deletedSessions = linkedSessionCount })
        : Results.NotFound(new { error = $"Agent '{slug}' not found." });
});

// GET /api/sessions — list persisted sessions for sidebar restoration.
app.MapGet("/api/sessions", () =>
{
    var agentsBySlug = store.ListAgents()
        .ToDictionary(agent => agent.Slug, StringComparer.OrdinalIgnoreCase);

    var sessions = store.ListSessions()
        .Select(session => ToSessionDto(session, agentsBySlug))
        .ToList();

    return Results.Ok(sessions);
});

app.MapGet("/api/sessions/{id}", (string id) =>
{
    var session = store.ListSessions().FirstOrDefault(item => item.SessionId == id);
    if (session is null)
        return Results.NotFound(new { error = $"Session '{id}' not found." });

    var agentsBySlug = store.ListAgents()
        .ToDictionary(agent => agent.Slug, StringComparer.OrdinalIgnoreCase);

    return Results.Ok(ToSessionDto(session, agentsBySlug));
});

app.MapDelete("/api/sessions/{id}", async (string id) =>
{
    var session = store.ListSessions().FirstOrDefault(item => item.SessionId == id);
    if (session is null)
        return Results.NotFound(new { error = $"Session '{id}' not found." });

    if (messageStreams.Keys.Any(key => key.StartsWith($"{id}/", StringComparison.Ordinal)))
        return Results.Conflict(new { error = $"Session '{id}' is currently streaming and cannot be deleted yet." });

    if (!store.DeleteSession(id))
        return Results.NotFound(new { error = $"Session '{id}' not found." });

    if (runners.TryRemove(id, out var runner))
        await runner.DisposeAsync();

    foreach (var streamKey in messageStreams.Keys.Where(key => key.StartsWith($"{id}/", StringComparison.Ordinal)).ToList())
    {
        if (messageStreams.TryRemove(streamKey, out var stream))
            stream.Writer.TryComplete();
    }

    return Results.Ok(new { sessionId = id, deleted = true });
});

// POST /api/sessions — create a session, return its ID.
app.MapPost("/api/sessions", async (CreateSessionRequest req, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.AgentId))
        return Results.BadRequest(new { error = "agentId is required." });

    var agentId = req.AgentId.Trim();

    var agentRecord = store.GetAgent(agentId);
    if (agentRecord is null)
        return Results.NotFound(new { error = $"Agent '{agentId}' not found." });
    if (!agentRecord.IsEnabled)
        return Results.Conflict(new { error = $"Agent '{agentId}' is disabled." });

    var sessionId = Guid.NewGuid().ToString("N")[..12];
    store.CreateSession(sessionId, agentRecord.Slug);

    // Spin up an agent runner and cache it.
    var runner = CreateRunner(agentRecord);
    await runner.InitializeAsync(ct);
    runners[sessionId] = runner;

    return Results.Ok(new { sessionId, persona = agentRecord.Name, agentId = agentRecord.Slug });
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

    var isAdeSession = conversation.AgentSlug.Equals(AdeAgentId, StringComparison.OrdinalIgnoreCase);

    // Get or create runner.
    AgentRunner? runner = null;
    if (!isAdeSession && !runners.TryGetValue(id, out runner))
    {
        var agentRecord = store.GetAgent(conversation.AgentSlug);
        if (agentRecord is null)
            return Results.NotFound(new { error = $"Agent definition '{conversation.AgentSlug}' not found." });

        runner = CreateRunner(agentRecord);
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
        AgentRunner? turnRunner = runner;
        var disposeTurnRunner = false;

        try
        {
            var assistantIndex = conversation.Messages.Count(message => message.Role == ChatRole.Assistant);
            var persistedEventLog = new List<StoredEventLogItem>();

            void RecordEvent(AgentEvent evt)
            {
                switch (evt)
                {
                    case ToolCallEvent toolCall:
                        persistedEventLog.Add(new StoredEventLogItem(toolCall, null));
                        break;
                    case ToolResultEvent toolResult:
                        {
                            for (var i = persistedEventLog.Count - 1; i >= 0; i--)
                            {
                                var item = persistedEventLog[i];
                                if (item.Event is ToolCallEvent pendingCall &&
                                    pendingCall.Tool == toolResult.Tool &&
                                    item.Result is null)
                                {
                                    persistedEventLog[i] = item with { Result = toolResult };
                                    return;
                                }
                            }

                            persistedEventLog.Add(new StoredEventLogItem(toolResult, toolResult));
                            break;
                        }
                    case StatusEvent status:
                        persistedEventLog.Add(new StoredEventLogItem(status, null));
                        break;
                    case UsageEvent usage:
                        persistedEventLog.Add(new StoredEventLogItem(usage, null));
                        break;
                    case PermissionRequestEvent permissionRequest:
                        persistedEventLog.Add(new StoredEventLogItem(permissionRequest, null));
                        break;
                }
            }

            void EmitStatus(string message)
            {
                var status = new StatusEvent(message);
                RecordEvent(status);
                channel.Writer.TryWrite(status);
            }

            // ── Ade routing ───────────────────────────────────────────────
            // If this session uses Ade, ask him whether a specialist is a better fit first.
            if (isAdeSession)
            {
                var adeRecord = store.GetAgent(conversation.AgentSlug);
                if (adeRecord is null)
                    throw new InvalidOperationException($"Agent definition '{conversation.AgentSlug}' not found.");

                if (runners.TryRemove(id, out var staleRunner))
                    await staleRunner.DisposeAsync();

                var availableAgents = new Dictionary<string, string>();
                foreach (var agentRec in store.ListAgents())
                {
                    if (agentRec.Slug.Equals(AdeAgentId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!agentRec.IsEnabled)
                        continue;
                    availableAgents[agentRec.Slug] = agentRec.Name + " — " + agentRec.Description;
                }

                EmitStatus("Checking who can handle this...");
                app.Logger.LogInformation(
                    "Ade routing started for session {SessionId}, message {MessageId}.",
                    id,
                    msgId);

                var routingAgent = new RoutingAgent(
                    new AnthropicBackend(new Anthropic.AnthropicClient(
                        new Anthropic.Core.ClientOptions
                        {
                            ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")!
                        })),
                    adeRecord.ToPersona());

                var decision = await routingAgent.RouteAsync(req.Message, availableAgents);

                if (decision.Agent is not null)
                {
                    var specialistRecord = store.GetAgent(decision.Agent);
                    if (specialistRecord is not null)
                    {
                        EmitStatus($"Passing this to {specialistRecord.Name}...");
                        app.Logger.LogInformation(
                            "Ade routed session {SessionId}, message {MessageId} to specialist {AgentSlug} ({AgentName}).",
                            id,
                            msgId,
                            specialistRecord.Slug,
                            specialistRecord.Name);

                        var specialistRunner = CreateRunner(specialistRecord);
                        await specialistRunner.InitializeAsync(CancellationToken.None);
                        turnRunner = specialistRunner;
                        disposeTurnRunner = true;
                        runners[id] = turnRunner;

                        // Use the rewritten prompt if available.
                        if (decision.RewrittenPrompt is not null)
                        {
                            conversation.ReplaceLastUser(decision.RewrittenPrompt);
                        }
                    }
                }
                else
                {
                    EmitStatus("I'll handle this myself...");
                    app.Logger.LogInformation(
                        "Ade kept session {SessionId}, message {MessageId} for direct handling.",
                        id,
                        msgId);

                    var directAdeRunner = CreateRunner(adeRecord with
                    {
                        SystemPrompt = BuildDirectResponseSystemPrompt(adeRecord),
                    });
                    await directAdeRunner.InitializeAsync(CancellationToken.None);
                    turnRunner = directAdeRunner;
                    disposeTurnRunner = true;
                    runners[id] = turnRunner;
                }

                app.Logger.LogInformation(
                    "Ade handling phase started for session {SessionId}, message {MessageId} with runner persona {PersonaName}.",
                    id,
                    msgId,
                    turnRunner?.Persona.Name);
            }

            var fullText = new System.Text.StringBuilder();
            await foreach (var evt in turnRunner!.StreamAsync(
                conversation.Messages,
                eventSink: e =>
                {
                    RecordEvent(e);
                    channel.Writer.TryWrite(e);
                },
                ct: CancellationToken.None))
            {
                RecordEvent(evt);
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
                store.SaveEventLog(id, assistantIndex, persistedEventLog);
                app.Logger.LogInformation(
                    "Assistant response persisted for session {SessionId}, message {MessageId}, length {Length}.",
                    id,
                    msgId,
                    fullText.Length);
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Message handling failed for session {SessionId}, message {MessageId}.", id, msgId);
            channel.Writer.TryWrite(new DoneEvent($"Error: {ex.Message}"));
        }
        finally
        {
            if (disposeTurnRunner && turnRunner is not null)
            {
                if (runners.TryGetValue(id, out var currentRunner) && ReferenceEquals(currentRunner, turnRunner))
                    runners.TryRemove(id, out _);

                await turnRunner.DisposeAsync();
            }

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

record CreateSessionRequest(string? AgentId);
record SendMessageRequest(string? Message);
record PermissionDecision(bool Allow);
record AgentEnabledRequest(bool IsEnabled);
record McpEnabledRequest(bool IsEnabled);
record AgentDefinitionRequest(
    string? Name,
    string? Description,
    string? Backend,
    string? Model,
    List<string>? McpServers,
    Dictionary<string, string>? PermissionPolicy,
    string? SystemPrompt,
    bool? IsEnabled);
record McpDefinitionRequest(
    string? Slug,
    string? Name,
    string? Description,
    string? Command,
    List<string>? Args,
    bool? IsEnabled);
