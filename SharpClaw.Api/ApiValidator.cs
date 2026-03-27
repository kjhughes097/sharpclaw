using SharpClaw.Api.Models;
using SharpClaw.Core;

namespace SharpClaw.Api;

internal static class ApiValidator
{
    internal static string? ValidateAgentRequest(SessionStore store, AgentDefinitionRequest req)
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
        if (backend is not ("anthropic" or "copilot" or "openai" or "openrouter"))
            return "backend must be 'anthropic', 'copilot', 'openai', or 'openrouter'.";

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
}