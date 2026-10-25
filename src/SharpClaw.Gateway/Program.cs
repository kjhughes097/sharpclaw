using SharpClaw.Gateway;

if (args.Contains("--healthcheck"))
    return await HealthCheckCommand.RunAsync();

var app = GatewayBuilder.Build(args);
GatewayBuilder.Migrate(app);
await app.RunAsync();
return 0;

/// <summary>Entry-point marker for test host discovery.</summary>
public partial class Program { }

internal static class HealthCheckCommand
{
    public static async Task<int> RunAsync()
    {
        var portValue = Environment.GetEnvironmentVariable("Listeners__ClientPort");
        var port = int.TryParse(portValue, out var p) ? p : 8080;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var resp = await http.GetAsync($"http://localhost:{port}/healthz");
            return resp.IsSuccessStatusCode ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }
}
