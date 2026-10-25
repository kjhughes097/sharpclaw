using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Providers;

namespace SharpClaw.Gateway;

public static partial class GatewayServices
{
    static partial void RegisterProviders(WebApplicationBuilder builder)
    {
        var providerType = builder.Configuration.GetValue<string>("Provider:Type") ?? "OpenRouter";

        if (string.Equals(providerType, "Fake", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddSingleton<IChatProvider, FakeProvider>();
        }
        else
        {
            builder.Services.AddHttpClient<IChatProvider, OpenRouterProvider>();
            builder.Services.AddSingleton<IStartupValidator, OpenRouterKeyValidator>();
        }
    }
}

public interface IStartupValidator
{
    void Validate();
}

public sealed class OpenRouterKeyValidator : IStartupValidator
{
    private readonly OpenRouterOptions _options;
    public OpenRouterKeyValidator(IOptions<OpenRouterOptions> options) => _options = options.Value;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("OpenRouter:ApiKey is required when Provider:Type=OpenRouter.");
    }
}
