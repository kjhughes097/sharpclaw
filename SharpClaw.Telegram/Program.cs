using Telegram.Bot;
using SharpClaw.Telegram;

var builder = WebApplication.CreateBuilder(args);

var botToken = builder.Configuration["Telegram:BotToken"]
    ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
    ?? throw new InvalidOperationException(
        "Telegram bot token is not configured. Set Telegram:BotToken or TELEGRAM_BOT_TOKEN.");

var webhookSecret = builder.Configuration["Telegram:WebhookSecret"]
    ?? Environment.GetEnvironmentVariable("TELEGRAM_WEBHOOK_SECRET")
    ?? string.Empty;

var sharpClawApiUrl = builder.Configuration["SharpClaw:ApiUrl"]
    ?? Environment.GetEnvironmentVariable("SHARPCLAW_API_URL")
    ?? throw new InvalidOperationException(
        "SharpClaw API URL is not configured. Set SharpClaw:ApiUrl or SHARPCLAW_API_URL.");

var sharpClawApiKey = builder.Configuration["SharpClaw:ApiKey"]
    ?? Environment.GetEnvironmentVariable("SHARPCLAW_API_KEY")
    ?? throw new InvalidOperationException(
        "SharpClaw API key is not configured. Set SharpClaw:ApiKey or SHARPCLAW_API_KEY.");

builder.Services.AddSingleton<ITelegramBotClient>(new TelegramBotClient(botToken));
builder.Services.AddHttpClient<SharpClawApiClient>(client =>
{
    client.BaseAddress = new Uri(sharpClawApiUrl.TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Add("X-Api-Key", sharpClawApiKey);
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddSingleton<SessionMappingStore>();
builder.Services.AddSingleton<TelegramUpdateHandler>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/telegram/webhook", async (
    HttpRequest request,
    TelegramUpdateHandler handler,
    ILogger<Program> logger) =>
{
    if (!string.IsNullOrEmpty(webhookSecret))
    {
        if (!request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var headerSecret) ||
            !string.Equals(headerSecret.ToString(), webhookSecret, StringComparison.Ordinal))
        {
            return Results.Unauthorized();
        }
    }

    Telegram.Bot.Types.Update? update;
    try
    {
        update = await request.ReadFromJsonAsync<Telegram.Bot.Types.Update>(JsonBotAPI.Options);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to parse Telegram update");
        return Results.BadRequest();
    }

    if (update is null)
        return Results.BadRequest();

    _ = Task.Run(() => handler.HandleUpdateAsync(update));
    return Results.Ok();
});

app.Run();
