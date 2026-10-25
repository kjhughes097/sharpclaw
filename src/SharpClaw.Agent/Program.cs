using Microsoft.Extensions.Logging;
using SharpClaw.Agent;

var config = AgentConfig.FromEnvironment();

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddJsonConsole(o => o.IncludeScopes = true);
    if (Enum.TryParse<LogLevel>(config.LogLevel, ignoreCase: true, out var level))
        builder.SetMinimumLevel(level);
});

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var gateway = new GatewayHttpClient(http, config);
var soul = new SoulProvider(config, loggerFactory.CreateLogger<SoulProvider>());
var loop = new AgentLoop(gateway, soul, config, loggerFactory.CreateLogger<AgentLoop>(), TimeProvider.System);

return await loop.RunAsync(cts.Token);
