using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpClaw.Agent.Tools.Workspace;
using SharpClaw.Contracts;
using SharpClaw.Sandbox.Infrastructure;
using SharpClaw.Sandbox.Ops;

namespace SharpClaw.Sandbox;

/// <summary>
/// The sandbox runner (external tools spec section 6): a small HTTP service that accepts operations
/// from the gateway only (bearer service secret, constant-time) and runs them against this agent's
/// workspace. It holds no credential except its own service secret, which never reaches a child.
/// </summary>
public static class RunnerApp
{
    public static WebApplication Build(RunnerOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        if (options.Secret.Length < RunnerOptions.MinSecretChars)
            throw new InvalidOperationException($"SANDBOX_SECRET must be at least {RunnerOptions.MinSecretChars} characters.");

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole();
        builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(options.Port));

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new WorkspaceRoot(options.WorkspacePath));
        builder.Services.AddSingleton(ChildEnvironment.Build());
        builder.Services.AddSingleton<IOutboundHttp>(_ => new ProxiedOutboundHttp(options.Proxy));
        builder.Services.AddSingleton<Fetcher>();
        builder.Services.AddSingleton<IRunnerOp, HttpFetchOp>();
        builder.Services.AddSingleton<IRunnerOp, DownloadOp>();
        configure?.Invoke(builder);

        var app = builder.Build();
        app.MapGet("/healthz", () => Results.Text("""{"status":"ok"}""", "application/json"));
        app.MapPost("/ops/{op}", RunAsync);
        return app;
    }

    private static async Task<IResult> RunAsync(
        string op, HttpContext http, RunnerOptions options, IEnumerable<IRunnerOp> ops, ILoggerFactory loggers, CancellationToken ct)
    {
        if (!Authorized(http.Request, options.Secret))
            return Results.Text("""{"error":{"code":"unauthorized","message":"Missing or invalid credentials."}}""", "application/json", statusCode: 401);
        var handler = ops.FirstOrDefault(o => o.Name == op);
        if (handler is null)
            return Results.Text("""{"error":{"code":"not_found","message":"Unknown operation."}}""", "application/json", statusCode: 404);

        JsonElement args;
        try
        {
            args = (await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct)).RootElement;
        }
        catch (JsonException)
        {
            return Results.Text(OpResult.Error("invalid_arguments", "Arguments must be a JSON object.").Json, "application/json");
        }
        if (args.ValueKind != JsonValueKind.Object)
            return Results.Text(OpResult.Error("invalid_arguments", "Arguments must be a JSON object.").Json, "application/json");

        var sw = Stopwatch.StartNew();
        OpResult result;
        try
        {
            result = await handler.RunAsync(args, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only the type: messages can carry command output or file content.
            loggers.CreateLogger("SharpClaw.Sandbox").LogWarning("op failed: op={Op} error={Error}", op, ex.GetType().Name);
            result = OpResult.Error("tool_failed", $"The operation failed ({ex.GetType().Name}).");
        }
        loggers.CreateLogger("SharpClaw.Sandbox").LogInformation("op: op={Op} ok={Ok} duration_ms={DurationMs}", op, result.Ok, sw.ElapsedMilliseconds);
        return Results.Text(result.Json, "application/json");
    }

    internal static bool Authorized(HttpRequest request, string secret)
    {
        var header = request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.Ordinal))
            return false;
        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(header[scheme.Length..].Trim()));
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }
}
