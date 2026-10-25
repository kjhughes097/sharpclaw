namespace SharpClaw.Contracts;

/// <summary>The single error shape used by every endpoint.</summary>
public sealed record ApiError(string Code, string Message);

public sealed record ApiErrorResponse(ApiError Error)
{
    public static ApiErrorResponse Of(string code, string message) => new(new ApiError(code, message));
}
