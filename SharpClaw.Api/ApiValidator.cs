using SharpClaw.Api.Models;
using SharpClaw.Core;

namespace SharpClaw.Api;

internal static class ApiValidator
{
    internal static string? ValidateAgentRequest(
        SessionStore store,
        IReadOnlyCollection<string> backendNames,
        AgentDefinitionRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return "name is required.";
        if (string.IsNullOrWhiteSpace(req.Description))
            return "description is required.";
        if (string.IsNullOrWhiteSpace(req.Backend))
            return "backend is required.";
        if (string.IsNullOrWhiteSpace(req.SystemPrompt))
            return "systemPrompt is required.";

        var backend = req.Backend.Trim().ToLowerInvariant();
        if (!backendNames.Contains(backend, StringComparer.OrdinalIgnoreCase))
        {
            if (backendNames.Count == 0)
                return "No backends are enabled. Configure at least one backend first.";

            var formattedNames = string.Join(", ", backendNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Select(name => $"'{name}'"));
            return $"backend must be {formattedNames}.";
        }

        var unknownMcp = ApiMapper.NormalizeStringList(req.McpServers)
            .FirstOrDefault(slug => store.GetMcp(slug) is null);
        if (unknownMcp is not null)
            return $"Unknown MCP '{unknownMcp}'.";

        return null;
    }

    internal static string? ValidateMcpRequest(McpDefinitionRequest req, bool creating)
    {
        if (creating && string.IsNullOrWhiteSpace(req.Slug))
            return "slug is required.";
        if (string.IsNullOrWhiteSpace(req.Name))
            return "name is required.";
        if (string.IsNullOrWhiteSpace(req.Description))
            return "description is required.";
        if (string.IsNullOrWhiteSpace(req.Command))
            return "command is required.";

        return null;
    }

    internal static string? ValidateTelegramSettingsRequest(UpdateTelegramSettingsRequest req)
    {
        if (req.AllowedUserIds is not null && req.AllowedUserIds.Any(id => id <= 0))
            return "allowedUserIds entries must be positive Telegram user IDs.";

        if (req.ClearMappingStorePath == true && !string.IsNullOrWhiteSpace(req.MappingStorePath))
            return "mappingStorePath and clearMappingStorePath cannot be set together.";

        return null;
    }

    internal static string? ValidateBackendSettingsRequest(UpdateBackendSettingsRequest req)
    {
        if (req.ClearApiKey == true && !string.IsNullOrWhiteSpace(req.ApiKey))
            return "apiKey and clearApiKey cannot be set together.";

        return null;
    }

    internal static string? ValidateAppSettingsRequest(UpdateAppSettingsRequest req)
    {
        if (req.ClearWorkspacePath == true && !string.IsNullOrWhiteSpace(req.WorkspacePath))
            return "workspacePath and clearWorkspacePath cannot be set together.";

        return null;
    }

    internal static string? ValidateSetupAuthRequest(SetupAuthRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username))
            return "username is required.";

        if (string.IsNullOrWhiteSpace(req.Password))
            return "password is required.";

        if (req.Password.Length < 10)
            return "password must be at least 10 characters.";

        if (!string.Equals(req.Password, req.ConfirmPassword, StringComparison.Ordinal))
            return "password and confirmPassword must match.";

        return null;
    }

    internal static string? ValidateLoginRequest(LoginRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username))
            return "username is required.";

        if (string.IsNullOrWhiteSpace(req.Password))
            return "password is required.";

        return null;
    }
}