using SharpClaw.Contracts;

namespace SharpClaw.Gateway.Infrastructure;

/// <summary>Helpers that produce the single JSON error shape with the right status code.</summary>
public static class ApiResults
{
    public static IResult Error(int status, string code, string message, Action<IHeaderDictionary>? headers = null)
        => new ApiErrorResult(status, code, message, headers);

    public static IResult ValidationFailed(string message) => Error(400, ErrorCodes.ValidationFailed, message);
    public static IResult Unsupported(string message) => Error(400, ErrorCodes.Unsupported, message);
    public static IResult Unauthorized(string message = "Missing or invalid credentials.")
        => Error(401, ErrorCodes.Unauthorized, message);
    public static IResult AgentDisabled(string message = "Agent is disabled.")
        => Error(403, ErrorCodes.AgentDisabled, message);
    public static IResult NotFound(string message = "Not found.") => Error(404, ErrorCodes.NotFound, message);
    public static IResult Conflict(string message) => Error(409, ErrorCodes.Conflict, message);
    public static IResult InternalError(string message = "Internal error.")
        => Error(500, ErrorCodes.InternalError, message);

    private sealed class ApiErrorResult : IResult
    {
        private readonly int _status;
        private readonly ApiErrorResponse _body;
        private readonly Action<IHeaderDictionary>? _headers;

        public ApiErrorResult(int status, string code, string message, Action<IHeaderDictionary>? headers)
        {
            _status = status;
            _body = ApiErrorResponse.Of(code, message);
            _headers = headers;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = _status;
            _headers?.Invoke(httpContext.Response.Headers);
            await httpContext.Response.WriteAsJsonAsync(_body, SharpClawJson.Options);
        }
    }
}
