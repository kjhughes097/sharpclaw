using System.Text;
using SharpClaw.Abstractions;
using SharpClaw.Models;

namespace SharpClaw.Tools;

public sealed class HttpProxyTool : ITool
{
    private readonly string _serviceName;
    private readonly ServiceEndpointDefinition _endpoint;
    private readonly int _port;

    public HttpProxyTool(string serviceName, ServiceEndpointDefinition endpoint, int port)
    {
        _serviceName = serviceName;
        _endpoint = endpoint;
        _port = port;

        Name = $"{serviceName.Replace("-", "_")}_{endpoint.Name.Replace("-", "_")}";
        Description = !string.IsNullOrEmpty(endpoint.Description)
            ? endpoint.Description
            : $"Calls {endpoint.Method} {endpoint.Path} on the {serviceName} service";
    }

    public string Name { get; }
    public string Description { get; }

    public IReadOnlyList<ToolParameterDefinition> Parameters { get; } =
        BuildParameters();

    public async Task<object?> ExecuteAsync(ToolCallContext context, CancellationToken ct = default)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var path = ResolvePath(context);
        var url = $"http://localhost:{_port}{path}";

        var request = new HttpRequestMessage(new HttpMethod(_endpoint.Method), url);

        // If POST/PUT/PATCH, include body from context
        if (_endpoint.Method is "POST" or "PUT" or "PATCH")
        {
            var body = context.GetString("body");
            if (!string.IsNullOrEmpty(body))
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
        }

        try
        {
            var response = await httpClient.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return $"Error: HTTP {(int)response.StatusCode} — {content}";

            return content;
        }
        catch (HttpRequestException ex)
        {
            return $"Error: Service '{_serviceName}' is not reachable — {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            return $"Error: Request to service '{_serviceName}' timed out";
        }
    }

    private string ResolvePath(ToolCallContext context)
    {
        var path = _endpoint.Path;

        // Replace path parameters like {id} from context arguments
        if (_endpoint.Parameters is not null)
        {
            foreach (var param in _endpoint.Parameters)
            {
                var value = context.GetString(param.Name);
                if (!string.IsNullOrEmpty(value))
                {
                    path = path.Replace($"{{{param.Name}}}", Uri.EscapeDataString(value));
                }
            }
        }

        return path;
    }

    private static IReadOnlyList<ToolParameterDefinition> BuildParameters() =>
        [new("body", "string", "Optional JSON body for POST/PUT/PATCH requests", Required: false)];
}
