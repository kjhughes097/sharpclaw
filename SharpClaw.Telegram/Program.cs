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

var sharpClawApiToken = Normalize(builder.Configuration["SharpClaw:ApiToken"])
    ?? Normalize(Environment.GetEnvironmentVariable("SHARPCLAW_API_TOKEN"))
    ?? throw new InvalidOperationException(
        "SharpClaw API token is not configured. Set SharpClaw:ApiToken or SHARPCLAW_API_TOKEN.");

var tokenProvider = new SharpClawAuthTokenProvider(sharpClawApiUrl, sharpClawApiToken);

var runtimeSettings = await TryLoadRuntimeSettingsAsync(sharpClawApiUrl, tokenProvider);

if (runtimeSettings is not null)
{
    var runtimeOverrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
        ["Telegram:IsEnabled"] = runtimeSettings.IsEnabled.ToString(),
        ["Telegram:MappingStorePath"] = runtimeSettings.MappingStorePath,
    };

    for (var i = 0; i < runtimeSettings.AllowedUserIds.Count; i++)
        runtimeOverrides[$"Telegram:AllowedUserIds:{i}"] = runtimeSettings.AllowedUserIds[i].ToString();

    for (var i = 0; i < runtimeSettings.AllowedUsernames.Count; i++)
        runtimeOverrides[$"Telegram:AllowedUsernames:{i}"] = runtimeSettings.AllowedUsernames[i];

    builder.Configuration.AddInMemoryCollection(runtimeOverrides);
}

var botToken = Normalize(runtimeSettings?.BotToken)
    ?? throw new InvalidOperationException(
        "Telegram bot token is not configured in SharpClaw runtime settings. Configure it in /api/integrations/telegram.");

builder.Services.AddSingleton<ITelegramBotClient>(new TelegramBotClient(botToken));
builder.Services.AddSingleton(tokenProvider);
builder.Services.AddTransient<SharpClawAuthHandler>();
builder.Services.AddHttpClient<SharpClawApiClient>(client =>
{
    client.BaseAddress = new Uri(sharpClawApiUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromMinutes(5);
})
    .AddHttpMessageHandler<SharpClawAuthHandler>();
builder.Services.AddSingleton<SessionMappingStore>();
builder.Services.AddSingleton<TelegramUpdateHandler>();
builder.Services.AddHostedService<TelegramPollingService>();

var host = builder.Build();
await host.RunAsync();

static async Task<TelegramRuntimeSettings?> TryLoadRuntimeSettingsAsync(string apiUrl, SharpClawAuthTokenProvider tokenProvider)
{
    try
    {
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        var authToken = await tokenProvider.GetTokenAsync();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
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
    IReadOnlyList<string> AllowedUsernames,
    string MappingStorePath);
