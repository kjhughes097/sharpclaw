using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Auth;

namespace SharpClaw.Gateway.Infrastructure;

/// <summary>
/// The client API's OpenAPI document (3.3 spec section 7): <c>GET /openapi/v1.json</c> on the
/// client listener, authenticated like <c>/api/*</c>, covering client routes only. The committed
/// snapshot in <c>contracts/openapi/</c> is the contract the UI's generated client is built from.
/// </summary>
public static class OpenApiSetup
{
    public const string DocumentName = "v1";
    public const string EventStreamDescription =
        "A text/event-stream of SharpClaw events. Event names and payloads are listed in docs/ui.md.";

    private static readonly int[] ErrorStatuses = [400, 401, 403, 404, 409, 500];

    public static void AddSharpClawOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(DocumentName, o =>
        {
            // Client routes only: agent (/agent, /v1), health and the document itself are left out.
            o.ShouldInclude = d => d.RelativePath?.StartsWith("api/", StringComparison.Ordinal) == true;
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Info = new OpenApiInfo
                {
                    Title = "SharpClaw client API",
                    Version = "v1",
                    Description = "The gateway's client listener API, used by the web UI and the CLI.",
                };
                // Kestrel decides the server; keep the snapshot independent of the test ports.
                doc.Servers = new List<OpenApiServer>();
                doc.Components ??= new OpenApiComponents();
                doc.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    ["sessionCookie"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.ApiKey,
                        In = ParameterLocation.Cookie,
                        Name = "sharpclaw_session",
                        Description = "Browser session from POST /api/auth/login. Unsafe methods also need csrfToken.",
                    },
                    ["csrfToken"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.ApiKey,
                        In = ParameterLocation.Header,
                        Name = "X-CSRF-Token",
                        Description = "The session's CSRF token; required with the session cookie on non-GET requests.",
                    },
                    ["adminKey"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        Description = "The admin key as a bearer token (CLI and scripts).",
                    },
                };
                doc.Security = new List<OpenApiSecurityRequirement>
                {
                    new() { [new OpenApiSecuritySchemeReference("adminKey", doc)] = [] },
                    new()
                    {
                        [new OpenApiSecuritySchemeReference("sessionCookie", doc)] = [],
                        [new OpenApiSecuritySchemeReference("csrfToken", doc)] = [],
                    },
                };
                return Task.CompletedTask;
            });
        });
    }

    /// <summary>Every client route can answer with the shared error object.</summary>
    public static RouteGroupBuilder ProducesApiErrors(this RouteGroupBuilder group)
    {
        foreach (var status in ErrorStatuses)
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiErrorResponse), ["application/json"]));
        return group;
    }

    public static RouteHandlerBuilder ProducesEventStream(this RouteHandlerBuilder builder) =>
        builder.Produces<string>(StatusCodes.Status200OK, "text/event-stream").WithDescription(EventStreamDescription);

    /// <summary>Serves the document on the client listener, behind the usual client authentication.</summary>
    public static void MapSharpClawOpenApi(this WebApplication app)
    {
        app.MapGroup("/openapi")
            .RequireListener(Listener.Client)
            .AddEndpointFilter(new ClientAuthFilter(ClientAuthMode.AdminKeyOrSession))
            .MapOpenApi("/{documentName}.json");
    }
}
