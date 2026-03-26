using SharpClaw.Api.Models;
using SharpClaw.Core;

namespace SharpClaw.Api;

internal static class ApiMapper
{
    internal const string AdeAgentId = "ade.agent.md";

    internal static string CreateAgentId(string name)
    {
        var slugChars = new List<char>();
        var previousWasDash = false;

        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                slugChars.Add(ch);
                previousWasDash = false;
                continue;
            }

            if (previousWasDash)
                continue;

            slugChars.Add('-');
            previousWasDash = true;
        }

        var slug = new string(slugChars.ToArray()).Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
            slug = "agent";

        return $"{slug}.agent.md";
    }

    internal static List<string> NormalizeStringList(IEnumerable<string>? values) => (values ?? [])
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    internal static PersonaDto ToPersonaDto(AgentRecord agent) =>
        new(
            agent.Slug,
            agent.Name,
            agent.Description,
            agent.Backend,
            agent.Model,
            agent.McpServers,
            agent.PermissionPolicy,
            agent.SystemPrompt,
            agent.IsEnabled);

    internal static AgentDto ToAgentDto(AgentRecord agent, int sessionCount) =>
        new(
            agent.Slug,
            agent.Name,
            agent.Description,
            agent.Backend,
            agent.Model,
            agent.McpServers,
            agent.PermissionPolicy,
            agent.SystemPrompt,
            agent.IsEnabled,
            sessionCount);

    internal static McpDto ToMcpDto(McpServerRecord mcp, int linkedAgentCount) =>
        new(
            mcp.Slug,
            mcp.Name,
            mcp.Description,
            mcp.Command,
            mcp.Args,
            mcp.IsEnabled,
            linkedAgentCount);

    internal static BackendModelsResponse ToBackendModelsDto(
        IReadOnlyList<(string Id, string DisplayName)> models,
        string source,
        DateTimeOffset? cachedAt = null,
        string? warning = null) =>
        new(
            models.Select(model => new BackendModelDto(model.Id, model.DisplayName)).ToList(),
            source,
            cachedAt,
            warning);

    internal static SessionDto ToSessionDto(SessionStore store, StoredSession session, Dictionary<string, AgentRecord> agentsBySlug)
    {
        var conversation = store.Load(session.SessionId);
        var eventLogs = store.LoadEventLogs(session.SessionId);
        var hasAgent = agentsBySlug.TryGetValue(session.AgentSlug, out var agentRecord);
        var personaName = hasAgent ? agentRecord!.Name : session.AgentSlug;

        return new SessionDto(
            session.SessionId,
            personaName,
            session.AgentSlug,
            session.CreatedAt,
            session.LastActivityAt,
            conversation?.Messages.Select(message => new MessageDto(
                message.Role == ChatRole.User ? "user" : "assistant",
                message.Content)).ToList() ?? [],
            eventLogs.Select(log => (IReadOnlyList<StoredEventLogItemDto>)log
                .Select(item => new StoredEventLogItemDto(item.Event, item.Result))
                .ToList()).ToList());
    }

    internal static AgentRecord ToAgentRecord(AgentDefinitionRequest req, string? slugOverride = null) => new(
        Slug: slugOverride ?? CreateAgentId(req.Name!.Trim()),
        Name: req.Name!.Trim(),
        Description: req.Description!.Trim(),
        Backend: req.Backend!.Trim().ToLowerInvariant(),
        Model: req.Model?.Trim() ?? string.Empty,
        McpServers: NormalizeStringList(req.McpServers),
        PermissionPolicy: (req.PermissionPolicy ?? new Dictionary<string, string>())
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
            .ToDictionary(kvp => kvp.Key.Trim(), kvp => kvp.Value.Trim(), StringComparer.OrdinalIgnoreCase),
        SystemPrompt: req.SystemPrompt!.Trim(),
        IsEnabled: req.IsEnabled ?? true);

    internal static McpServerRecord ToMcpRecord(McpDefinitionRequest req, string? slugOverride = null) => new(
        Slug: slugOverride ?? req.Slug!.Trim(),
        Name: req.Name!.Trim(),
        Description: req.Description!.Trim(),
        Command: req.Command!.Trim(),
        Args: NormalizeStringList(req.Args),
        IsEnabled: req.IsEnabled ?? true);

    internal static string BuildDirectResponseSystemPrompt(AgentRecord agentRecord)
    {
        return $"""
            You are {agentRecord.Name}. {agentRecord.Description}

            Help the user directly and answer in normal conversational language.
            Do not return a routing JSON object or mention internal routing behavior unless it genuinely helps the user.
            """;
    }
}