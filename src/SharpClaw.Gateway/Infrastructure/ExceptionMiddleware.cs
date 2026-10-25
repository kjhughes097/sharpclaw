using SharpClaw.Contracts;

namespace SharpClaw.Gateway.Infrastructure;

/// <summary>Converts unhandled exceptions into the single error shape; never leaks details.</summary>
public sealed class ExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _logger;

    public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task Invoke(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected; nothing to write.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception");
            if (context.Response.HasStarted)
                throw;
            context.Response.Clear();
            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(
                ApiErrorResponse.Of(ErrorCodes.InternalError, "Internal error."),
                SharpClawJson.Options);
        }
    }
}
