using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpClaw.Contracts;

/// <summary>Shared JSON configuration: camelCase, enums as strings.</summary>
public static class SharpClawJson
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }
}
