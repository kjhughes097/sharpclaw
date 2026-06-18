using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Telegram.Bot;
using SharpClaw.Abstractions;
using SharpClaw.Api;
using SharpClaw.Audio;
using SharpClaw.Auditing;
using SharpClaw.Commands;
using SharpClaw.Configuration;
using SharpClaw.Execution;
using SharpClaw.Interactions;
using SharpClaw.Loading;
using SharpClaw.Mcp;
using SharpClaw.Memory;
using SharpClaw.Registry;
using SharpClaw.Scheduling;
using SharpClaw.Sessions;
using SharpClaw.Telegram;
using SharpClaw.Tools;
using SharpClaw.Workers;
using SharpClaw.Workspace;

var builder = WebApplication.CreateBuilder(args);

// -- CORS (allow Web UI access from any origin) --
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// -- Configuration binding --
builder.Services.Configure<SharpClawOptions>(builder.Configuration.GetSection(SharpClawOptions.SectionName));
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
builder.Services.Configure<OpenTelemetryOptions>(builder.Configuration.GetSection(OpenTelemetryOptions.SectionName));
builder.Services.Configure<AnthropicOptions>(builder.Configuration.GetSection(AnthropicOptions.SectionName));
builder.Services.Configure<AnthropicAdminMcpOptions>(builder.Configuration.GetSection(AnthropicAdminMcpOptions.SectionName));
builder.Services.Configure<SemanticMemoryOptions>(builder.Configuration.GetSection(SemanticMemoryOptions.SectionName));
builder.Services.Configure<SttOptions>(builder.Configuration.GetSection(SttOptions.SectionName));
builder.Services.Configure<TicketWorkerOptions>(builder.Configuration.GetSection(TicketWorkerOptions.SectionName));

// -- Logging: Console with custom format --
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(opts =>
{
    opts.TimestampFormat = "yyyy-MM-dd HH:mm:ss : ";
    opts.SingleLine = true;
});

// -- OpenTelemetry --
var otelEndpoint = builder.Configuration.GetValue<string>("OpenTelemetry:Endpoint") ?? "http://localhost:4317";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("SharpClaw"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(o =>
        {
            o.Endpoint = new Uri(otelEndpoint);
            o.Protocol = OtlpExportProtocol.Grpc;
        }))
    .WithMetrics(metrics => metrics
        .AddMeter("SharpClaw.Tokens")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(o =>
        {
            o.Endpoint = new Uri(otelEndpoint);
            o.Protocol = OtlpExportProtocol.Grpc;
        }))
    .WithLogging(logging => logging
        .AddOtlpExporter(o =>
        {
            o.Endpoint = new Uri(otelEndpoint);
            o.Protocol = OtlpExportProtocol.Grpc;
        }),
        options => options.IncludeFormattedMessage = true);

// -- Registries (singletons) --
builder.Services.AddSingleton<IAgentRegistry, AgentRegistry>();
builder.Services.AddSingleton<IToolRegistry, ToolRegistry>();
builder.Services.AddSingleton<IMcpRegistry, McpRegistry>();
builder.Services.AddSingleton<ISkillRegistry, SkillRegistry>();
builder.Services.AddSingleton<IServiceRegistry, ServiceRegistry>();

// -- Loaders --
builder.Services.AddSingleton<AgentLoader>();
builder.Services.AddSingleton<McpLoader>();
builder.Services.AddSingleton<SkillLoader>();
builder.Services.AddSingleton<ServiceLoader>();
builder.Services.AddSingleton<ProjectLoader>();
// -- Execution --
builder.Services.AddSingleton<CopilotProvider>();
builder.Services.AddSingleton<ILlmProvider>(sp => sp.GetRequiredService<CopilotProvider>());

var anthropicApiKey = builder.Configuration.GetValue<string>("Anthropic:ApiKey");
if (!string.IsNullOrEmpty(anthropicApiKey))
{
    builder.Services.AddSingleton<AnthropicProvider>();
    builder.Services.AddSingleton<ILlmProvider>(sp => sp.GetRequiredService<AnthropicProvider>());
}

builder.Services.AddSingleton<AgentRunner>();

// -- Scheduling --
builder.Services.AddSingleton<SchedulingContextAccessor>();
builder.Services.AddSingleton<ScheduleStore>();

// -- Tools --
builder.Services.AddSingleton<SpawnAgentTool>();
builder.Services.AddSingleton<SkillExecutorTool>();
builder.Services.AddSingleton<ScheduleTaskTool>();
builder.Services.AddSingleton<CancelTaskTool>();
builder.Services.AddSingleton<WorkspaceFileTool>();
builder.Services.AddSingleton<WorkspaceWriteTool>();
builder.Services.AddSingleton<ProjectTool>();
builder.Services.AddSingleton<TicketTool>();

// -- Sessions & Interactions --
builder.Services.AddSingleton<AgentSessionRegistry>();
builder.Services.AddSingleton<ChannelFanOutService>();
builder.Services.AddSingleton<CommandRouter>();
builder.Services.AddSingleton<AgentInvoker>();

// -- Commands --
builder.Services.AddSingleton<ICommand, SwitchAgentCommand>();
builder.Services.AddSingleton<ICommand, PingCommand>();
builder.Services.AddSingleton<ICommand, ReloadCommand>();
builder.Services.AddSingleton<ICommand, HelpCommand>();
builder.Services.AddSingleton<ICommand, ListAgentsCommand>();
builder.Services.AddSingleton<ICommand, ListMcpsCommand>();
builder.Services.AddSingleton<ICommand, ListToolsCommand>();
builder.Services.AddSingleton<ICommand, ListMcpToolsCommand>();
builder.Services.AddSingleton<ICommand, ListSkillsCommand>();
builder.Services.AddSingleton<ICommand, ListSchedulesCommand>();
builder.Services.AddSingleton<ICommand, ListCronCommand>();
builder.Services.AddSingleton<ICommand, CancelScheduleCommand>();
builder.Services.AddSingleton<ICommand, ListServicesCommand>();
builder.Services.AddSingleton<ICommand, RestartCommand>();
builder.Services.AddSingleton<ICommand, NewSessionCommand>();
builder.Services.AddSingleton<ICommand, TicketCommand>();

// -- Memory & Auditing --
builder.Services.AddSingleton<MemoryService>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<TranscriptService>();
builder.Services.AddSingleton<TokenUsageService>();

// -- Semantic Memory (conditional) --
var semanticMemoryEnabled = builder.Configuration.GetValue<bool>("SemanticMemory:Enabled");
var anthropicConfigured = !string.IsNullOrEmpty(builder.Configuration.GetValue<string>("Anthropic:ApiKey"));
if (semanticMemoryEnabled)
{
    builder.Services.AddSingleton<EmbeddingService>();
    builder.Services.AddSingleton<SemanticMemoryStore>();
    builder.Services.AddSingleton<SemanticMemoryService>();
    builder.Services.AddSingleton<MemoryImportService>();
    builder.Services.AddHostedService<MemoryDecayWorker>();

    // Extraction requires Anthropic API key
    if (anthropicConfigured)
    {
        builder.Services.AddSingleton<MemoryExtractionService>();
    }
    else
    {
        builder.Services.AddSingleton<MemoryExtractionService?>(sp => null);
    }
}
else
{
    builder.Services.AddSingleton<SemanticMemoryService?>(sp => null);
    builder.Services.AddSingleton<MemoryExtractionService?>(sp => null);
    builder.Services.AddSingleton<MemoryImportService?>(sp => null);
}

// -- STT (Speech-to-Text) — conditional --
var sttEnabled = builder.Configuration.GetValue<bool>("Stt:Enabled");
if (sttEnabled)
{
    builder.Services.AddHttpClient();
    builder.Services.AddSingleton<AudioConverter>();
    builder.Services.AddSingleton<WhisperModelDownloader>();
    builder.Services.AddSingleton<WhisperTranscriptionService>();
    builder.Services.AddSingleton<ITranscriptionService>(sp =>
        sp.GetRequiredService<WhisperTranscriptionService>());
}
else
{
    builder.Services.AddSingleton<ITranscriptionService?>(sp => null);
}

// -- Workspace --
builder.Services.AddSingleton<WorkspaceInitialiser>();

// -- MCP Server (self-hosted) --
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<MemoryMcpTools>()
    .WithTools<SemanticMemoryMcpTools>()
    .WithTools<AnthropicAdminMcpTools>();

// -- Telegram --
var telegramToken = builder.Configuration.GetValue<string>("Telegram:BotToken");
if (!string.IsNullOrEmpty(telegramToken) && telegramToken != "YOUR_BOT_TOKEN_HERE")
{
    builder.Services.AddSingleton<TelegramAgentRouter>();
    builder.Services.AddSingleton<ITelegramBotClient>(sp => new TelegramBotClient(telegramToken));
    builder.Services.AddHostedService<TelegramService>();
    builder.Services.AddSingleton<ITaskResultDelivery, TelegramTaskDelivery>();
    builder.Services.AddSingleton<SendTelegramTool>();
}
builder.Services.AddSingleton<ITaskResultDelivery, WebTaskDelivery>();

// -- Background Workers --
builder.Services.AddSingleton<RegistryWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RegistryWorker>());
builder.Services.AddHostedService<SchedulerWorker>();
builder.Services.AddHostedService<TicketAssignmentWorker>();
builder.Services.AddSingleton<ServiceRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServiceRunner>());

var app = builder.Build();

// -- Initialise workspace after registries are loaded --
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStarted.Register(() =>
{
    var workspace = app.Services.GetRequiredService<WorkspaceInitialiser>();
    workspace.Initialise();
});

// -- Register built-in tools --
var toolRegistry = app.Services.GetRequiredService<IToolRegistry>();
toolRegistry.Register(app.Services.GetRequiredService<SpawnAgentTool>());
toolRegistry.Register(app.Services.GetRequiredService<SkillExecutorTool>());
toolRegistry.Register(app.Services.GetRequiredService<ScheduleTaskTool>());
toolRegistry.Register(app.Services.GetRequiredService<CancelTaskTool>());
toolRegistry.Register(app.Services.GetRequiredService<WorkspaceFileTool>());
toolRegistry.Register(app.Services.GetRequiredService<WorkspaceWriteTool>());
toolRegistry.Register(app.Services.GetRequiredService<ProjectTool>());
toolRegistry.Register(app.Services.GetRequiredService<TicketTool>());

if (!string.IsNullOrEmpty(telegramToken) && telegramToken != "YOUR_BOT_TOKEN_HERE")
{
    toolRegistry.Register(app.Services.GetRequiredService<SendTelegramTool>());
}

// -- Static files (Web UI) --
app.UseCors();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30),
});
app.UseStaticFiles();

// -- Endpoints --
app.MapMcp("/mcp");

app.MapGet("/health", () => "SharpClaw is running.");

app.MapGet("/agents", (IAgentRegistry registry) =>
    registry.GetAll().OrderBy(a => a.Name).Select(a => new { a.Name, a.Description }));

// -- Web UI API --
app.MapAgentEndpoints();
app.MapChatEndpoints();
app.MapChatWebSocketEndpoints();
app.MapMcpEndpoints();
app.MapToolEndpoints();
app.MapSkillEndpoints();
app.MapTaskEndpoints();
app.MapConfigEndpoints();
app.MapProjectEndpoints();
app.MapTokenEndpoints();

app.MapFallbackToFile("index.html");

app.Run();
