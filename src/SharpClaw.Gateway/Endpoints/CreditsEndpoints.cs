using SharpClaw.Contracts;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Endpoints;

/// <summary><c>GET /api/credits</c> (3.3.1 spec section 3.4): the cached OpenRouter balance; never calls upstream.</summary>
public static class CreditsEndpoints
{
    public static void Map(RouteGroupBuilder client)
    {
        client.MapGet("/credits", (CreditsState state) => Results.Json(state.ToResponse(), SharpClawJson.Options))
            .WithName("getCredits").WithTags("credits").Produces<CreditsResponse>();
    }
}
