using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Providers;

namespace SharpClaw.Gateway;

public static class ProviderRegistration
{
    /// <summary>Registers the configured <see cref="IChatProvider"/> (<c>Provider:Type</c>: OpenRouter or Fake).</summary>
    public static WebApplicationBuilder AddSharpClawProviders(this WebApplicationBuilder builder)
    {
        var providerType = builder.Configuration.GetValue<string>("Provider:Type") ?? "OpenRouter";

        if (string.Equals(providerType, "Fake", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddSingleton<IChatProvider, FakeProvider>();
        }
        else
        {
            builder.Services.AddHttpClient<IChatProvider, OpenRouterProvider>();
            builder.Services.AddSingleton<IStartupCheck, OpenRouterKeyValidator>();
        }
        return builder;
    }
}

public sealed class OpenRouterKeyValidator(IOptions<OpenRouterOptions> options) : IStartupCheck
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(options.Value.ApiKey))
            throw new InvalidOperationException("OpenRouter:ApiKey is required when Provider:Type=OpenRouter.");
    }
}
