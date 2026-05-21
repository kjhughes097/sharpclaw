using SharpClaw.Abstractions;

namespace SharpClaw.Api;

internal static class ToolEndpoints
{
    public static void MapToolEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tools").WithTags("Tools");

        group.MapGet("/", (IToolRegistry registry) =>
            registry.GetAll().OrderBy(t => t.Name).Select(t => new
            {
                t.Name,
                t.Description,
                Parameters = t.Parameters.Select(p => new
                {
                    p.Name,
                    p.Description,
                    p.Type,
                    p.Required,
                }),
            }));

        group.MapGet("/{name}", (string name, IToolRegistry registry) =>
        {
            var tool = registry.Get(name);
            if (tool is null) return Results.NotFound();

            return Results.Ok(new
            {
                tool.Name,
                tool.Description,
                Parameters = tool.Parameters.Select(p => new
                {
                    p.Name,
                    p.Description,
                    p.Type,
                    p.Required,
                }),
            });
        });
    }
}
