using SharpClaw.Telegram;
using System.Net.Http.Headers;
using System.Text.Json;
using Telegram.Bot;

var builder = Host.CreateApplicationBuilder(args);

static string? Normalize(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');

var sharpClawApiUrl = Normalize(builder.Configuration["SharpClaw:ApiUrl"])
    ?? Normalize(Environment.GetEnvironmentVariable("SHARPCLAW_API_URL"))
    ?? throw new InvalidOperationException(
        "SharpClaw API URL is not configured. Set SharpClaw:ApiUrl or SHARPCLAW_API_URL.");

var sharpClawApiKey = Normalize(builder.Configuration["SharpClaw:ApiKey"])
    ?? Normalize(Environment.GetEnvironmentVariable("SHARPCLAW_API_KEY"))
    ?? throw new InvalidOperationException(
        "SharpClaw API key is not configured. Set SharpClaw:ApiKey or SHARPCLAW_API_KEY.");

var runtimeSettings = await TryLoadRuntimeSettingsAsync(sharpClawApiUrl, sharpClawApiKey);

if (string.IsNullOrWhiteSpace(builder.Configuration["Telegram:IsEnabled"]) &&
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TELEGRAM_ENABLED")) &&
    runtimeSettings is not null)
{
    Environment.SetEnvironmentVariable("TELEGRAM_ENABLED", runtimeSettings.IsEnabled.ToString());
}

if (string.IsNullOrWhiteSpace(builder.Configuration["Telegram:AllowedUserIds"]) &&
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TELEGRAM_ALLOWED_USER_IDS")) &&
    runtimeSettings is not null &&
    runtimeSettings.AllowedUserIds.Count > 0)
{
    Environment.SetEnvironmentVariable("TELEGRAM_ALLOWED_USER_IDS", string.Join(',', runtimeSettings.AllowedUserIds));
}

if (string.IsNullOrWhiteSpace(builder.Configuration["Telegram:AllowedUsernames"]) &&
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TELEGRAM_ALLOWED_USERNAMES")) &&
    runtimeSettings is not null &&
    runtimeSettings.AllowedUsernames.Count > 0)
{
    Environment.SetEnvironmentVariable("TELEGRAM_ALLOWED_USERNAMES", string.Join(',', runtimeSettings.AllowedUsernames));
}

var botToken = Normalize(builder.Configuration["Telegram:BotToken"])
    ?? Normalize(Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"))
    ?? Normalize(runtimeSettings?.BotToken)
    ?? throw new InvalidOperationException(
        "Telegram bot token is not configured. Set Telegram:BotToken, TELEGRAM_BOT_TOKEN, or configure it in /api/integrations/telegram.");

builder.Services.AddSingleton<ITelegramBotClient>(new TelegramBotClient(botToken));
builder.Services.AddHttpClient<SharpClawApiClient>(client =>
{
    client.BaseAddress = new Uri(sharpClawApiUrl.TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Add("X-Api-Key", sharpClawApiKey);
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddSingleton<SessionMappingStore>();
builder.Services.AddSingleton<TelegramUpdateHandler>();
builder.Services.AddHostedService<TelegramPollingService>();

var host = builder.Build();
await host.RunAsync();

static async Task<TelegramRuntimeSettings?> TryLoadRuntimeSettingsAsync(string apiUrl, string apiKey)
{
    try
    {
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        httpClient.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.GetAsync("api/integrations/telegram/runtime");
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<TelegramRuntimeSettings>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
    }
    catch
    {
        return null;
    }
}

internal sealed record TelegramRuntimeSettings(
    bool IsEnabled,
    string? BotToken,
    IReadOnlyList<long> AllowedUserIds,
    IReadOnlyList<string> AllowedUsernames);
