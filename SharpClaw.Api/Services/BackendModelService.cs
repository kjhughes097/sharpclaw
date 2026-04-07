using System.Collections.Concurrent;
using SharpClaw.Api.Models;
using SharpClaw.Core;

namespace SharpClaw.Api.Services;

public sealed class BackendModelService(
    BackendRegistry backendRegistry,
    BackendSettingsService backendSettingsService)
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, IReadOnlyList<BackendModelInfo> Models)> _backendModelCache =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<ApiResponse<IApiPayload>> GetModelsAsync(string backend, CancellationToken cancellationToken)
    {
        var normalizedBackend = backend.Trim().ToLowerInvariant();
        if (!backendRegistry.TryGet(normalizedBackend, out var provider))
        {
            return new ApiResponse<IApiPayload>(
                StatusCodes.Status400BadRequest,
                new ErrorResponse(backendRegistry.BuildSupportedBackendsMessage()));
        }

        try
        {
            backendSettingsService.EnsureBackendConfigured(normalizedBackend);
            var models = await provider.ListModelsAsync(cancellationToken);

            _backendModelCache[normalizedBackend] = (DateTimeOffset.UtcNow, models);

            return new ApiResponse<IApiPayload>(
                StatusCodes.Status200OK,
                ApiMapper.ToBackendModelsDto(models, source: "live"));
        }
        catch (Exception ex) when (_backendModelCache.TryGetValue(normalizedBackend, out var cachedModels))
        {
            return new ApiResponse<IApiPayload>(
                StatusCodes.Status200OK,
                ApiMapper.ToBackendModelsDto(
                    cachedModels.Models,
                    source: "cache",
                    cachedAt: cachedModels.CachedAt,
                    warning: ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return new ApiResponse<IApiPayload>(StatusCodes.Status400BadRequest, new ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("Not authenticated", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("authenticate first", StringComparison.OrdinalIgnoreCase))
            {
                return new ApiResponse<IApiPayload>(StatusCodes.Status400BadRequest, new ErrorResponse(ex.Message));
            }

            return new ApiResponse<IApiPayload>(
                StatusCodes.Status502BadGateway,
                new ProblemResponse(
                    $"Failed to load models for backend '{normalizedBackend}'.",
                    ex.Message));
        }
    }
}