using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Gateway.Infrastructure;

public enum Listener { Client, Agent }

/// <summary>
/// Endpoint filter enforcing listener separation by the local port the connection arrived on.
/// A request to a route group on the wrong port returns 404 (clients control the Host header,
/// so it must not be used for this decision).
/// </summary>
public sealed class ListenerFilter : IEndpointFilter
{
    private readonly Listener _required;

    public ListenerFilter(Listener required) => _required = required;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var ports = http.RequestServices.GetRequiredService<IOptions<ListenerOptions>>().Value;
        var localPort = http.Connection.LocalPort;
        var arrivedOn = localPort == ports.AgentPort ? Listener.Agent
            : localPort == ports.ClientPort ? Listener.Client
            : (Listener?)null;

        if (arrivedOn != _required)
            return ApiResults.NotFound();

        return await next(context);
    }
}

public static class ListenerRouteExtensions
{
    public static RouteGroupBuilder RequireListener(this RouteGroupBuilder group, Listener listener)
        => (RouteGroupBuilder)group.AddEndpointFilter(new ListenerFilter(listener));
}
