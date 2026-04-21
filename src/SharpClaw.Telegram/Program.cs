using SharpClaw.Telegram;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection("Telegram"));
builder.Services.AddHttpClient<SharpClawApiClient>();
builder.Services.AddHostedService<TelegramWorker>();

var host = builder.Build();
host.Run();
