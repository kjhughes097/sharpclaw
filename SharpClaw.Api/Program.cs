using Scalar.AspNetCore;
using SharpClaw.Api.Middleware;
using SharpClaw.Api.Services;
using SharpClaw.Core;

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
builder.Services.AddSingleton<BackendModelService>();
builder.Services.AddSingleton<SessionRuntimeService>();
builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseMiddleware<ApiKeyMiddleware>();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapControllers();

app.Run();
