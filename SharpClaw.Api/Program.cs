using Scalar.AspNetCore;
using SharpClaw.Api.Middleware;
using SharpClaw.Api.Services;
using SharpClaw.Anthropic;
using SharpClaw.Copilot;
using SharpClaw.Core;
using SharpClaw.OpenAI;
using SharpClaw.OpenRouter;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? Environment.GetEnvironmentVariable("SHARPCLAW_DB_CONNECTION");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Database connection is not configured. Set ConnectionStrings:DefaultConnection or SHARPCLAW_DB_CONNECTION. " +
        "For Docker Compose runs, ensure POSTGRES_DB, POSTGRES_USER, and POSTGRES_PASSWORD are set.");
}

builder.Services.AddSingleton(new SessionStore(connectionString));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IAgentBackendProvider, AnthropicBackendProvider>();
builder.Services.AddSingleton<IAgentBackendProvider, CopilotBackendProvider>();
builder.Services.AddSingleton<IAgentBackendProvider, OpenAIBackendProvider>();
builder.Services.AddSingleton<IAgentBackendProvider, OpenRouterBackendProvider>();
builder.Services.AddSingleton<BackendRegistry>();
builder.Services.AddSingleton<BackendSettingsService>();
builder.Services.AddSingleton<BackendModelService>();
builder.Services.AddSingleton<SessionRuntimeService>();
builder.Services.AddSingleton<PasswordHashService>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseMiddleware<JwtAuthMiddleware>();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapControllers();

app.Run();
