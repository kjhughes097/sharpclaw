using System.Collections.Concurrent;
using System.Text.Json;
using Anthropic.Core;
using GitHub.Copilot.SDK;
using SharpClaw.Api.Models;

namespace SharpClaw.Api.Services;

public sealed class BackendModelService(IHttpClientFactory httpClientFactory)
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, IReadOnlyList<(string Id, string DisplayName)> Models)> _backendModelCache =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<ApiResponse<IApiPayload>> GetModelsAsync(string backend, CancellationToken cancellationToken)
    {
        var normalizedBackend = backend.Trim().ToLowerInvariant();
        if (normalizedBackend is not ("anthropic" or "copilot"))
        {
            return new ApiResponse<IApiPayload>(
                StatusCodes.Status400BadRequest,
                new ErrorResponse("backend must be either 'anthropic' or 'copilot'."));
        }

        try
        {
            var models = normalizedBackend switch
            {
                "anthropic" => await ListAnthropicModelsAsync(cancellationToken),
                "copilot" => await ListCopilotModelsAsync(cancellationToken),
                _ => [],
            };

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

    private static CopilotClient CreateCopilotClient()
    {
        var opts = new CopilotClientOptions
        {
            Cwd = Environment.GetEnvironmentVariable("SHARPCLAW_WORKSPACE") ?? Environment.CurrentDirectory,
        };

        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            token = Environment.GetEnvironmentVariable("GITHUB_COPILOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            token = TryGetGhToken();

        if (!string.IsNullOrWhiteSpace(token))
            opts.GitHubToken = token;

        return new CopilotClient(opts);
    }

    private static string? TryGetGhToken()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
                return null;

            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            return proc.ExitCode == 0 && output.StartsWith("gho_") ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<(string Id, string DisplayName)>> ListCopilotModelsAsync(CancellationToken cancellationToken)
    {
        await using var client = CreateCopilotClient();
        await client.StartAsync(cancellationToken);

        var response = await client.ListModelsAsync(cancellationToken);
        return response
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .Select(model => (
                model.Id,
                string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name))
            .ToList();
    }

    private async Task<IReadOnlyList<(string Id, string DisplayName)>> ListAnthropicModelsAsync(CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("ANTHROPIC_API_KEY is not set.");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        using var httpClient = httpClientFactory.CreateClient();
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Anthropic model list request failed with {(int)response.StatusCode}: {responseBody}");
        }

        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var models = new List<(string Id, string DisplayName)>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement))
                continue;

            var id = idElement.GetString();
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var displayName = item.TryGetProperty("display_name", out var nameElement)
                ? nameElement.GetString()
                : null;

            models.Add((id, string.IsNullOrWhiteSpace(displayName) ? id : displayName));
        }

        return models;
    }
}