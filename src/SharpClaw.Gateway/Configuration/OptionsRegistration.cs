using System.Net;
using Microsoft.Extensions.Options;

namespace SharpClaw.Gateway.Configuration;

/// <summary>A check that must pass before the gateway serves anything; a failure stops startup.</summary>
public interface IStartupCheck
{
    void Validate();
}

/// <summary>
/// Binds every options section, validates it (DataAnnotations on the option classes plus the
/// cross-field rules below), and fails startup with a readable message on bad configuration,
/// rather than misbehaving later.
/// </summary>
public static class OptionsRegistration
{
    public static WebApplicationBuilder AddSharpClawOptions(this WebApplicationBuilder builder)
    {
        Add<ListenerOptions, ListenerOptionsValidator>(builder, ListenerOptions.Section)
            .Validate(o => o.ClientPort != o.AgentPort,
                "Listeners:ClientPort and Listeners:AgentPort must be different ports.");
        Add<DatabaseOptions, DatabaseOptionsValidator>(builder, DatabaseOptions.Section);
        Add<AuthOptions, AuthOptionsValidator>(builder, AuthOptions.Section)
            .Validate(o => o.ForwardedHeaders.KnownProxies.All(p => IPAddress.TryParse(p, out _)),
                "Auth:ForwardedHeaders:KnownProxies must contain IP addresses only.");
        Add<ProviderOptions, ProviderOptionsValidator>(builder, ProviderOptions.Section);
        Add<OpenRouterOptions, OpenRouterOptionsValidator>(builder, OpenRouterOptions.Section);
        Add<FakeOptions, FakeOptionsValidator>(builder, FakeOptions.Section);
        Add<InboxOptions, InboxOptionsValidator>(builder, InboxOptions.Section);
        Add<MessagesOptions, MessagesOptionsValidator>(builder, MessagesOptions.Section);
        Add<ApprovalsOptions, ApprovalsOptionsValidator>(builder, ApprovalsOptions.Section);
        Add<ChatOptions, ChatOptionsValidator>(builder, ChatOptions.Section);
        Add<EventsOptions, EventsOptionsValidator>(builder, EventsOptions.Section);
        Add<ToolsOptions, ToolsOptionsValidator>(builder, ToolsOptions.Section);
        Add<ContextOptions, ContextOptionsValidator>(builder, ContextOptions.Section)
            .Validate(o => o.ReplyReserveTokens < o.DefaultWindowTokens,
                "Context:ReplyReserveTokens must be smaller than Context:DefaultWindowTokens.");
        Add<ModelsOptions, ModelsOptionsValidator>(builder, ModelsOptions.Section);
        Add<CreditsOptions, CreditsOptionsValidator>(builder, CreditsOptions.Section);
        Add<ToolBrokerOptions, ToolBrokerOptionsValidator>(builder, ToolBrokerOptions.Section);
        builder.Services.AddSingleton<IValidateOptions<ToolBrokerOptions>, ProblemValidator<ToolBrokerOptions>>();
        Add<WebSearchOptions, WebSearchOptionsValidator>(builder, WebSearchOptions.Section);
        Add<GitHubOptions, ProblemValidator<GitHubOptions>>(builder, GitHubOptions.Section);
        Add<EgressOptions, EgressOptionsValidator>(builder, EgressOptions.Section);
        builder.Services.AddSingleton<IValidateOptions<EgressOptions>, ProblemValidator<EgressOptions>>();
        Add<AgentMessagingOptions, AgentMessagingOptionsValidator>(builder, AgentMessagingOptions.Section);
        builder.Services.AddSingleton<IValidateOptions<AgentMessagingOptions>, ProblemValidator<AgentMessagingOptions>>();
        builder.Services.AddSingleton<IStartupCheck, SandboxSettingsCheck>();
        builder.Services.AddOptions<UiOptions>().Bind(builder.Configuration.GetSection(UiOptions.Section));
        return builder;
    }

    private static OptionsBuilder<T> Add<T, TValidator>(WebApplicationBuilder builder, string section)
        where T : class
        where TValidator : class, IValidateOptions<T>
    {
        builder.Services.AddSingleton<IValidateOptions<T>, TValidator>();
        builder.Services.AddSingleton<IStartupCheck, OptionsStartupCheck<T>>();
        return builder.Services.AddOptions<T>().Bind(builder.Configuration.GetSection(section));
    }

    /// <summary>Reading the value runs every registered validator (throws <see cref="OptionsValidationException"/>).</summary>
    private sealed class OptionsStartupCheck<T>(IOptions<T> options) : IStartupCheck where T : class
    {
        public void Validate() => _ = options.Value;
    }
}

// Source-generated DataAnnotations validators ([ValidateObjectMembers] recurses into nested options).
[OptionsValidator] public sealed partial class ListenerOptionsValidator : IValidateOptions<ListenerOptions>;
[OptionsValidator] public sealed partial class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>;
[OptionsValidator] public sealed partial class AuthOptionsValidator : IValidateOptions<AuthOptions>;
[OptionsValidator] public sealed partial class ProviderOptionsValidator : IValidateOptions<ProviderOptions>;
[OptionsValidator] public sealed partial class OpenRouterOptionsValidator : IValidateOptions<OpenRouterOptions>;
[OptionsValidator] public sealed partial class FakeOptionsValidator : IValidateOptions<FakeOptions>;
[OptionsValidator] public sealed partial class InboxOptionsValidator : IValidateOptions<InboxOptions>;
[OptionsValidator] public sealed partial class MessagesOptionsValidator : IValidateOptions<MessagesOptions>;
[OptionsValidator] public sealed partial class ApprovalsOptionsValidator : IValidateOptions<ApprovalsOptions>;
[OptionsValidator] public sealed partial class ChatOptionsValidator : IValidateOptions<ChatOptions>;
[OptionsValidator] public sealed partial class EventsOptionsValidator : IValidateOptions<EventsOptions>;
[OptionsValidator] public sealed partial class ToolsOptionsValidator : IValidateOptions<ToolsOptions>;
[OptionsValidator] public sealed partial class ContextOptionsValidator : IValidateOptions<ContextOptions>;
[OptionsValidator] public sealed partial class CreditsOptionsValidator : IValidateOptions<CreditsOptions>;
[OptionsValidator] public sealed partial class ToolBrokerOptionsValidator : IValidateOptions<ToolBrokerOptions>;
[OptionsValidator] public sealed partial class EgressOptionsValidator : IValidateOptions<EgressOptions>;
[OptionsValidator] public sealed partial class WebSearchOptionsValidator : IValidateOptions<WebSearchOptions>;
[OptionsValidator] public sealed partial class AgentMessagingOptionsValidator : IValidateOptions<AgentMessagingOptions>;

/// <summary>
/// Rules across the sandbox settings: the egress listener gets its own port, and a sandbox's proxy
/// secret (sandbox → egress) differs from its service secret (gateway → runner), so one leaked
/// value can't stand in for the other.
/// </summary>
public sealed class SandboxSettingsCheck(
    IOptions<ListenerOptions> listeners, IOptions<EgressOptions> egress, IOptions<ToolBrokerOptions> broker) : IStartupCheck
{
    public void Validate()
    {
        var e = egress.Value;
        if (e.Enabled && (e.Port == listeners.Value.ClientPort || e.Port == listeners.Value.AgentPort))
            throw new OptionsValidationException(EgressOptions.Section, typeof(EgressOptions),
                ["Egress:Port must differ from Listeners:ClientPort and Listeners:AgentPort."]);
        foreach (var (name, sandbox) in e.Configured)
            if (broker.Value.ForAgent(name)?.SandboxSecret is { Length: > 0 } serviceSecret && serviceSecret == sandbox.Secret)
                throw new OptionsValidationException(EgressOptions.Section, typeof(EgressOptions),
                    [$"Egress:Sandboxes:{name}:Secret must differ from ToolBroker:Agents:{name}:SandboxSecret."]);
    }
}

/// <summary>Options whose cross-field rules are a single <c>Problem()</c> check.</summary>
public interface IHasProblem
{
    /// <summary>Null when valid; otherwise the validation message.</summary>
    string? Problem();
}

/// <summary>Runs <see cref="IHasProblem.Problem"/> alongside the generated DataAnnotations validator.</summary>
public sealed class ProblemValidator<T> : IValidateOptions<T> where T : class, IHasProblem
{
    public ValidateOptionsResult Validate(string? name, T options) =>
        options.Problem() is { } problem ? ValidateOptionsResult.Fail(problem) : ValidateOptionsResult.Success;
}

/// <summary>Hand-written: the rules are about the list as a whole, which DataAnnotations can't express.</summary>
public sealed class ModelsOptionsValidator : IValidateOptions<ModelsOptions>
{
    public ValidateOptionsResult Validate(string? name, ModelsOptions options) =>
        options.Problem() is { } problem ? ValidateOptionsResult.Fail(problem) : ValidateOptionsResult.Success;
}
