using SharpClaw.Anthropic;
using SharpClaw.Api.Models;
using SharpClaw.Copilot;
using SharpClaw.Core;
using SharpClaw.OpenAI;
using SharpClaw.OpenRouter;

namespace SharpClaw.Api.Services;

public sealed class BackendSettingsService(SessionStore store, BackendRegistry backendRegistry)
{
    private sealed record BackendBinding(IReadOnlyList<string> CredentialKeys, bool RequiresApiKey);

    private static readonly IReadOnlyDictionary<string, BackendBinding> BackendBindings =
        new Dictionary<string, BackendBinding>(StringComparer.OrdinalIgnoreCase)
        {
            [AnthropicBackendProvider.Name] = new([AnthropicBackendProvider.ApiKeyEnvVar], true),
            [OpenAIBackendProvider.Name] = new([OpenAIBackendProvider.ApiKeyEnvVar], true),
            [OpenRouterBackendProvider.Name] = new([OpenRouterBackendProvider.ApiKeyEnvVar], true),
            [CopilotBackendProvider.Name] = new([
                CopilotBackendProvider.GitHubTokenEnvVar,
                CopilotBackendProvider.CopilotTokenEnvVar,
            ], true),
        };

    public IReadOnlyList<BackendSettingsDto> ListSettings()
    {
        var storedSettings = store.ListBackendIntegrationSettings()
            .ToDictionary(item => item.Backend, StringComparer.OrdinalIgnoreCase);

        return backendRegistry.BackendNames
            .Select(backendName =>
            {
                var binding = GetBinding(backendName);
                var hasStored = storedSettings.TryGetValue(backendName, out var stored);
                var apiKey = hasStored ? stored!.ApiKey : null;

                return ApiMapper.ToBackendSettingsDto(new BackendIntegrationSettings(
                    Backend: backendName,
                    IsEnabled: hasStored && stored!.IsEnabled,
                    ApiKey: apiKey,
                    UpdatedAt: hasStored ? stored!.UpdatedAt : null),
                    requiresApiKey: binding?.RequiresApiKey ?? false);
            })
            .ToList();
    }

    public IReadOnlySet<string> EnabledBackendNames()
    {
        return ListSettings()
            .Where(item => item.IsEnabled)
            .Select(item => item.Backend)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public ApiResponse<IApiPayload> UpdateSettings(string backend, UpdateBackendSettingsRequest request)
    {
        var normalizedBackend = backend.Trim().ToLowerInvariant();
        if (!backendRegistry.TryGet(normalizedBackend, out _))
        {
            return new ApiResponse<IApiPayload>(
                StatusCodes.Status400BadRequest,
                new ErrorResponse(backendRegistry.BuildSupportedBackendsMessage()));
        }

        var validationError = ApiValidator.ValidateBackendSettingsRequest(request);
        if (validationError is not null)
            return new ApiResponse<IApiPayload>(StatusCodes.Status400BadRequest, new ErrorResponse(validationError));

        var existing = store.GetBackendIntegrationSettings(normalizedBackend);
        var requestedKey = string.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey.Trim();
        var apiKey = request.ClearApiKey == true
            ? null
            : requestedKey ?? existing?.ApiKey;

        var updated = new BackendIntegrationSettings(
            Backend: normalizedBackend,
            IsEnabled: request.IsEnabled,
            ApiKey: apiKey,
            UpdatedAt: DateTimeOffset.UtcNow);

        store.UpsertBackendIntegrationSettings(updated);

        return new ApiResponse<IApiPayload>(
            StatusCodes.Status200OK,
            ApiMapper.ToBackendSettingsDto(updated, requiresApiKey: GetBinding(normalizedBackend)?.RequiresApiKey ?? false));
    }

    public void EnsureBackendConfigured(string backend)
    {
        var normalizedBackend = backend.Trim().ToLowerInvariant();
        if (!backendRegistry.TryGet(normalizedBackend, out _))
            throw new InvalidOperationException(backendRegistry.BuildSupportedBackendsMessage());

        var binding = GetBinding(normalizedBackend);
        var settings = store.GetBackendIntegrationSettings(normalizedBackend)
            ?? new BackendIntegrationSettings(normalizedBackend, false, null, null);

        if (!settings.IsEnabled)
        {
            throw new InvalidOperationException(
                $"Backend '{normalizedBackend}' is disabled. Enable it in Configure > Backends before use.");
        }

        if (binding is null)
            return;

        if (binding.RequiresApiKey && string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException(
                $"Backend '{normalizedBackend}' is enabled, but no API key is configured.");
        }

        foreach (var credentialKey in binding.CredentialKeys)
            BackendProviderUtilities.SetConfiguredValue(credentialKey, settings.ApiKey!);
    }

    private static BackendBinding? GetBinding(string backend)
    {
        return BackendBindings.TryGetValue(backend, out var binding) ? binding : null;
    }
}
