using SharpClaw.Api.Tools;
using SharpClaw.Copilot;
using SharpClaw.Core;
using SharpClaw.Llm;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ──────────────────────────────────────────────────

var dataRoot = builder.Configuration["SharpClaw:DataRoot"]
    ?? Path.Combine(AppContext.BaseDirectory, "data");
var agentsDir = builder.Configuration["SharpClaw:AgentsDir"]
    ?? Path.Combine(AppContext.BaseDirectory, "agents");
var projectsRoot = Path.Combine(dataRoot, "projects");
var workspaceRoot = builder.Configuration["SharpClaw:WorkspaceRoot"]
    ?? Path.Combine(dataRoot, "workspace");
var knowledgeRoot = Path.Combine(dataRoot, "knowledge");
var apiKey = builder.Configuration["SharpClaw:ApiKey"] ?? "";
var anthropicKey = builder.Configuration["SharpClaw:AnthropicApiKey"] ?? "";
var githubToken = builder.Configuration["SharpClaw:GitHubToken"];

// ── Core Services ──────────────────────────────────────────────────

// Parse agent definitions
var agents = AgentFileParser.LoadAll(agentsDir);
var projectManager = new ProjectManager(projectsRoot);
var workspaceManager = new WorkspaceManager(workspaceRoot);
var chatManager = new ChatManager(projectsRoot);
var memoryManager = new MemoryManager(projectsRoot, knowledgeRoot);
var routerService = new RouterService(agents);

// Tool registry
var toolRegistry = new ToolRegistry();
toolRegistry.Register(new FilesystemToolProvider(dataRoot));
toolRegistry.Register(new WebSearchToolProvider());

// LLM services
var llmService = new GenericLlmService(anthropicKey);
CopilotLlmService? copilotService = null;
if (!string.IsNullOrEmpty(githubToken))
    copilotService = new CopilotLlmService(githubToken);

var llmServices = new Dictionary<string, ILlmService>(StringComparer.OrdinalIgnoreCase)
{
    ["llm"] = llmService,
};
if (copilotService is not null)
    llmServices["copilot"] = copilotService;

// Register singletons for DI
builder.Services.AddSingleton(projectManager);
builder.Services.AddSingleton(workspaceManager);
builder.Services.AddSingleton(chatManager);
builder.Services.AddSingleton(memoryManager);
builder.Services.AddSingleton(routerService);
builder.Services.AddSingleton(toolRegistry);
builder.Services.AddSingleton<IReadOnlyDictionary<string, ILlmService>>(llmServices);
builder.Services.AddSingleton<IReadOnlyList<AgentDefinition>>(agents);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

app.UseCors();

// ── API Key Middleware ──────────────────────────────────────────────

if (!string.IsNullOrEmpty(apiKey))
{
    app.Use(async (context, next) =>
    {
        // Skip auth for CORS preflight
        if (context.Request.Method == "OPTIONS")
        {
            await next();
            return;
        }

        var providedKey = context.Request.Headers["X-Api-Key"].FirstOrDefault()
            ?? context.Request.Query["api_key"].FirstOrDefault();

        if (!string.Equals(providedKey, apiKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or missing API key" });
            return;
        }

        await next();
    });
}

// ── Endpoints ──────────────────────────────────────────────────────

// Health check
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

// Agents
app.MapGet("/api/agents", (RouterService router) =>
    router.GetAgents().Select(a => new
    {
        a.Slug,
        a.Name,
        a.Description,
        a.Service,
        a.Model,
        a.Tools,
        a.SystemPrompt,
    }));

// Projects
app.MapGet("/api/projects", (ProjectManager pm) => pm.ListProjects());
app.MapPost("/api/projects", (ProjectManager pm, CreateProjectRequest req) =>
{
    var project = pm.CreateProject(req.Name);
    return Results.Created($"/api/projects/{project.Slug}", project);
});
app.MapGet("/api/projects/{slug}", (ProjectManager pm, string slug) =>
{
    var project = pm.GetProject(slug);
    return project is null ? Results.NotFound() : Results.Ok(project);
});
app.MapDelete("/api/projects/{slug}", (ProjectManager pm, string slug) =>
    pm.DeleteProject(slug) ? Results.NoContent() : Results.NotFound());

// Chats
app.MapGet("/api/projects/{projectSlug}/chats", (ChatManager cm, string projectSlug) =>
    cm.ListChats(projectSlug));
app.MapPost("/api/projects/{projectSlug}/chats", (ChatManager cm, string projectSlug, CreateChatRequest req) =>
{
    var chat = cm.CreateChat(projectSlug, req.Title);
    return Results.Created($"/api/projects/{projectSlug}/chats/{chat.Slug}", chat);
});
app.MapGet("/api/projects/{projectSlug}/chats/{chatSlug}", (ChatManager cm, string projectSlug, string chatSlug) =>
{
    var chat = cm.GetChat(projectSlug, chatSlug);
    return chat is null ? Results.NotFound() : Results.Ok(chat);
});
app.MapGet("/api/projects/{projectSlug}/chats/{chatSlug}/messages",
    (ChatManager cm, string projectSlug, string chatSlug) =>
        cm.GetMessages(projectSlug, chatSlug));
app.MapDelete("/api/projects/{projectSlug}/chats/{chatSlug}",
    (ChatManager cm, string projectSlug, string chatSlug) =>
        cm.DeleteChat(projectSlug, chatSlug) ? Results.NoContent() : Results.NotFound());

// Workspace
app.MapGet("/api/workspace/status", (WorkspaceManager wm) =>
{
    var json = wm.ReadStatusCards();
    return json is null ? Results.Ok(new object[0]) : Results.Content(json, "application/json");
});
app.MapGet("/api/workspace", (WorkspaceManager wm) => wm.ListCategories());
app.MapGet("/api/workspace/{category}", (WorkspaceManager wm, string category) =>
    wm.ListProjects(category).Select(p => new
    {
        p.Slug,
        p.Name,
        p.Category,
        Status = p.Status.ToString().ToLowerInvariant(),
        p.CreatedAt,
        p.LastModifiedAt,
        p.TotalTokens,
        p.Icon,
        p.Image,
        p.Collaborators,
    }));
app.MapGet("/api/workspace/{category}/{slug}", (WorkspaceManager wm, string category, string slug) =>
{
    var project = wm.GetProject(category, slug);
    return project is null ? Results.NotFound() : Results.Ok(project);
});
app.MapGet("/api/workspace/{category}/{slug}/readme", (WorkspaceManager wm, string category, string slug) =>
{
    var readme = wm.GetReadme(category, slug);
    return readme is null ? Results.NotFound() : Results.Ok(new { content = readme });
});

// Knowledge
app.MapGet("/api/knowledge", (MemoryManager mm) =>
    new { facts = mm.ReadKnowledge("facts.md"), learned = mm.ReadKnowledge("learned.md"), archived = mm.ReadKnowledge("archived-projects.md") });
app.MapPut("/api/knowledge/{fileName}", async (MemoryManager mm, string fileName, HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var content = await reader.ReadToEndAsync();
    mm.WriteKnowledge(fileName, content);
    return Results.NoContent();
});

// Chat — the main endpoint
app.MapPost("/api/chat", async (
    HttpContext ctx,
    RouterService router,
    ChatManager cm,
    ProjectManager pm,
    MemoryManager mm,
    ToolRegistry tools,
    IReadOnlyDictionary<string, ILlmService> llmSvcs) =>
{
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";
    var ct = ctx.RequestAborted;

    // Parse request — supports both JSON and multipart/form-data
    string message;
    string? projectSlugParam, chatSlugParam, agentSlugParam;
    var formFiles = new List<IFormFile>();

    if (ctx.Request.HasFormContentType)
    {
        var form = await ctx.Request.ReadFormAsync(ct);
        message = form["message"].ToString();
        projectSlugParam = form["projectSlug"].FirstOrDefault();
        chatSlugParam = form["chatSlug"].FirstOrDefault();
        agentSlugParam = form["agentSlug"].FirstOrDefault();
        formFiles.AddRange(form.Files);
    }
    else
    {
        var req = await ctx.Request.ReadFromJsonAsync<ChatRequest>(ct);
        if (req is null)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "Invalid request body" });
            return;
        }
        message = req.Message;
        projectSlugParam = req.ProjectSlug;
        chatSlugParam = req.ChatSlug;
        agentSlugParam = req.AgentSlug;
    }

    // Resolve project (default to "general")
    var projectSlug = projectSlugParam ?? "general";
    if (pm.GetProject(projectSlug) is null)
        pm.CreateProject(projectSlug);

    // Resolve or create chat
    string chatSlug;
    if (chatSlugParam is not null)
    {
        chatSlug = chatSlugParam;
    }
    else
    {
        var title = message.Length > 50 ? message[..50] : message;
        var chat = cm.CreateChat(projectSlug, title);
        chatSlug = chat.Slug;
    }

    // Route to specialist agent (need agent slug before saving files)
    var currentAgent = cm.GetChat(projectSlug, chatSlug)?.LastAgent;
    ILlmService routerLlm = llmSvcs.GetValueOrDefault("llm") ?? llmSvcs.Values.First();
    var agent = agentSlugParam is not null
        ? router.GetAgent(agentSlugParam) ?? await router.RouteAsync(message, currentAgent, routerLlm, ct)
        : await router.RouteAsync(message, currentAgent, routerLlm, ct);

    // Save uploaded files to agent's files directory
    var attachments = new List<ChatAttachment>();
    foreach (var file in formFiles)
    {
        await using var stream = file.OpenReadStream();
        var attachment = await ChatManager.SaveAttachmentAsync(
            dataRoot, agent.Slug, file.FileName, file.ContentType, stream, ct);
        attachments.Add(attachment);
    }

    // Save user message (with attachment metadata if any)
    var userMsg = new ChatMessage(ChatRole.User, message,
        Attachments: attachments.Count > 0 ? attachments : null);
    cm.AppendMessage(projectSlug, chatSlug, userMsg);

    // Emit chat slug so frontend knows which chat we're in
    await WriteSseEvent(ctx.Response, "chat_info", new { projectSlug, chatSlug });

    await WriteSseEvent(ctx.Response, "status", new { message = $"Routing to {agent.Name}..." });

    // Resolve LLM service for this agent
    if (!llmSvcs.TryGetValue(agent.Service, out var llm))
    {
        await WriteSseEvent(ctx.Response, "error", new { message = $"LLM service '{agent.Service}' not configured." });
        return;
    }

    // Assemble context + system prompt
    var memoryContext = mm.AssembleContext(projectSlug, chatSlug);
    var systemPrompt = agent.SystemPrompt;
    if (memoryContext.Length > 0)
        systemPrompt = systemPrompt + "\n\n" + memoryContext;

    // Get conversation history
    var history = cm.GetMessages(projectSlug, chatSlug);

    // Get tool schemas for this agent
    var toolSchemas = tools.GetSchemas(agent.Tools);

    // Stream the response
    var responseContent = new System.Text.StringBuilder();
    var totalInputTokens = 0;
    var totalOutputTokens = 0;
    string? usageProvider = null;
    await foreach (var evt in llm.StreamAsync(agent.Model, systemPrompt, history, toolSchemas,
        (call, ct2) => tools.DispatchAsync(call, ct2), ct))
    {
        switch (evt)
        {
            case TokenEvent token:
                responseContent.Append(token.Text);
                await WriteSseEvent(ctx.Response, "token", new { text = token.Text });
                break;
            case ToolCallEvent tc:
                await WriteSseEvent(ctx.Response, "tool_call", new { tool = tc.Tool, input = tc.Input });
                break;
            case ToolResultEvent tr:
                await WriteSseEvent(ctx.Response, "tool_result", new { tool = tr.Tool, result = tr.Result, is_error = tr.IsError });
                break;
            case UsageEvent usage:
                totalInputTokens += usage.InputTokens;
                totalOutputTokens += usage.OutputTokens;
                usageProvider = usage.Provider;
                await WriteSseEvent(ctx.Response, "usage", new { provider = usage.Provider, input_tokens = usage.InputTokens, output_tokens = usage.OutputTokens });
                break;
            case StatusEvent status:
                await WriteSseEvent(ctx.Response, "status", new { message = status.Message });
                break;
            case DoneEvent:
                break;
        }
    }

    // Save assistant message
    var assistantMsg = new ChatMessage(ChatRole.Assistant, responseContent.ToString(), agent.Slug);
    cm.AppendMessage(projectSlug, chatSlug, assistantMsg);

    // Save token usage
    if (totalInputTokens > 0 || totalOutputTokens > 0)
    {
        var usageRecord = new TokenUsageRecord(usageProvider ?? "unknown", totalInputTokens, totalOutputTokens, agent.Slug, DateTimeOffset.UtcNow);
        cm.AppendUsage(projectSlug, chatSlug, usageRecord);
    }

    // Post-turn memory updates (fire-and-forget, non-blocking)
    _ = Task.Run(async () =>
    {
        try
        {
            var summary = $"- User: {(message.Length > 100 ? message[..100] + "..." : message)}\n- Agent response: {(responseContent.Length > 100 ? responseContent.ToString()[..100] + "..." : responseContent.ToString())}";
            mm.AppendChatLog(projectSlug, chatSlug, agent.Name, summary);
            mm.AppendProjectLog(projectSlug, agent.Name, summary);
        }
        catch { /* best effort */ }
    }, CancellationToken.None);

    await WriteSseEvent(ctx.Response, "done", new { agent = agent.Slug, input_tokens = totalInputTokens, output_tokens = totalOutputTokens });
});

app.Run();

// ── Helpers ────────────────────────────────────────────────────────

static async Task WriteSseEvent(HttpResponse response, string eventType, object data)
{
    var json = System.Text.Json.JsonSerializer.Serialize(data);
    await response.WriteAsync($"event: {eventType}\ndata: {json}\n\n");
    await response.Body.FlushAsync();
}

// ── Request DTOs ───────────────────────────────────────────────────

record ChatRequest(string Message, string? ProjectSlug = null, string? ChatSlug = null, string? AgentSlug = null);
record CreateProjectRequest(string Name);
record CreateChatRequest(string Title);
