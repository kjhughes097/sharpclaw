using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpClaw.Contracts;

/// <summary>
/// The one JSON configuration for every wire shape: web defaults, camelCase, enums as camelCase
/// strings, and strict numbers (a number is a JSON number, never a string). The gateway's request
/// binding applies the same settings via <see cref="Apply"/>, so requests and responses agree.
/// </summary>
public static class SharpClawJson
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create() => Apply(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>Applies the SharpClaw settings to existing options (e.g. ASP.NET's HTTP JSON options).</summary>
    public static JsonSerializerOptions Apply(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        // Strict numbers keep request parsing strict and the OpenAPI schemas plain integers.
        o.NumberHandling = JsonNumberHandling.Strict;
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }
}
