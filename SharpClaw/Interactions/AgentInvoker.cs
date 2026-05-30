using SharpClaw.Abstractions;
using SharpClaw.Auditing;
using SharpClaw.Commands;
using SharpClaw.Execution;
using SharpClaw.Models;
using SharpClaw.Scheduling;
using SharpClaw.Sessions;

namespace SharpClaw.Interactions;

public sealed class AgentInvoker(
    IAgentRegistry agentRegistry,
    ISkillRegistry skillRegistry,
    AgentRunner runner,
    CommandRouter commandRouter,
    AuditService auditService,
    TranscriptService transcriptService,
    SchedulingContextAccessor schedulingContextAccessor,
    ILogger<AgentInvoker> logger)
{
    public async Task<(string? SwitchedTo, string? ResponseText)> InvokeAsync(
        AgentSession session,
        string prompt,
        CancellationToken ct)
    {
        return await InvokeAsync(session, prompt, null, ct);
    }

    public async Task<(string? SwitchedTo, string? ResponseText)> InvokeAsync(
        AgentSession session,
        string prompt,
        SchedulingContext? channelContext,
        CancellationToken ct)
    {
        var requestStartedAt = DateTimeOffset.UtcNow;

        await transcriptService.LogAsync(
            session.AgentId,
            session.SessionId,
            "request",
            prompt,
            new TranscriptMetadata(
                Source: channelContext?.ChannelType.ToString()),
            ct);

        // Try commands first (no LLM call)
        var commandAgentId = session.AgentId;
        var cmdContext = new CommandContext(session.SessionId, prompt, session.AgentId);
        var cmdResult = await commandRouter.TryExecuteAsync(cmdContext, ct);

        if (cmdResult is not null)
        {
            if (cmdResult.SwitchedToAgent is not null)
                session.SetAgent(cmdResult.SwitchedToAgent);

            var commandResponseText = cmdResult.ResponseText ?? string.Empty;

            await transcriptService.LogAsync(
                commandAgentId,
                session.SessionId,
                "response",
                commandResponseText,
                new TranscriptMetadata(
                    Source: channelContext?.ChannelType.ToString(),
                    Success: true,
                    DurationMs: (DateTimeOffset.UtcNow - requestStartedAt).TotalMilliseconds,
                    IsCommand: true),
                ct);

            if (cmdResult.ResponseText is not null)
            {
                await session.PublishAsync(new AgentMessage(
                    session.SessionId, Guid.NewGuid().ToString(),
                    MessageOrigin.Agent, session.AgentId,
                    cmdResult.ResponseText, DateTimeOffset.UtcNow), ct);
            }

            return (cmdResult.SwitchedToAgent, cmdResult.ResponseText);
        }

        // Resolve agent
        var agent = agentRegistry.Get(session.AgentId);
        logger.LogDebug("Invoking agent {Agent} for session {Session}", session.AgentId, session.SessionId);
        if (agent is null)
        {
            var errorMsg = $"[No agent registered with name '{session.AgentId}']";
            await session.PublishAsync(new AgentMessage(
                session.SessionId, Guid.NewGuid().ToString(),
                MessageOrigin.Agent, session.AgentId,
                errorMsg, DateTimeOffset.UtcNow), ct);
            return (null, errorMsg);
        }

        logger.LogInformation(
            "Session {SessionId} using agent {Agent} (llm={Llm}, model={Model}, tools={ToolCount}, mcps={McpCount}, skills={SkillCount})",
            session.SessionId,
            agent.Name,
            agent.Llm ?? "copilot",
            agent.Model ?? "<default>",
            agent.ToolNames.Count,
            agent.McpNames.Count,
            agent.SkillNames.Count);

        // Audit the request
        await auditService.LogAsync(session.AgentId, AuditEntryType.Request, prompt, ct);

        // Set scheduling context so tools can access channel info
        schedulingContextAccessor.Current = channelContext;

        // Build system prompt with skills injected
        var systemPrompt = BuildSystemPromptWithSkills(agent);

        // First turn: create session; subsequent: reuse
        if (session.LlmSession is null)
        {
            logger.LogInformation("Creating LLM session for {Agent} in session {SessionId}", agent.Name, session.SessionId);

            var request = new AgentRunRequest(
                Prompt: prompt,
                Llm: agent.Llm,
                Model: agent.Model,
                SystemPromptOverride: systemPrompt,
                McpServerNames: agent.McpNames.Count > 0 ? agent.McpNames : null,
                ToolNames: agent.ToolNames.Count > 0 ? agent.ToolNames : null,
                LazyMcpNames: agent.LazyMcpNames.Count > 0 ? agent.LazyMcpNames : null
            );
            var llmSession = await runner.CreateSessionAsync(request, ct);
            session.SetLlmSession(llmSession);

            logger.LogInformation(
                "Created LLM session {LlmSessionId} for agent {Agent} in conversation {SessionId}",
                llmSession.SessionId,
                agent.Name,
                session.SessionId);
        }
        else
        {
            logger.LogInformation(
                "Reusing LLM session {LlmSessionId} for agent {Agent} in conversation {SessionId}",
                session.LlmSession.SessionId,
                agent.Name,
                session.SessionId);
        }

        var result = await runner.SendAsync(session.LlmSession!, prompt, agent.Llm, ct);

        var responseText = result.Success
            ? result.Response
            : $"[Agent error: {result.Error}]";

        if (result.Success && string.IsNullOrWhiteSpace(responseText))
        {
            logger.LogWarning(
                "LLM session {LlmSessionId} for agent {Agent} returned an empty response",
                session.LlmSession!.SessionId,
                agent.Name);
            responseText = "[No response text returned by model. Please retry.]";
        }

        await transcriptService.LogAsync(
            session.AgentId,
            session.SessionId,
            "response",
            responseText ?? string.Empty,
            new TranscriptMetadata(
                Source: channelContext?.ChannelType.ToString(),
                LlmProvider: agent.Llm ?? "copilot",
                Model: agent.Model,
                ToolCount: agent.ToolNames.Count,
                McpCount: agent.McpNames.Count,
                Success: result.Success,
                Error: result.Error,
                DurationMs: (DateTimeOffset.UtcNow - requestStartedAt).TotalMilliseconds,
                IsCommand: false,
                InputTokens: result.InputTokens,
                OutputTokens: result.OutputTokens),
            ct);

        // Audit the response
        await auditService.LogAsync(session.AgentId, AuditEntryType.Response, responseText ?? string.Empty, ct);

        await session.PublishAsync(new AgentMessage(
            session.SessionId, Guid.NewGuid().ToString(),
            MessageOrigin.Agent, session.AgentId,
            responseText ?? string.Empty, DateTimeOffset.UtcNow), ct);

        return (null, responseText);
    }

    private string? BuildSystemPromptWithSkills(IAgent agent)
    {
        var basePrompt = agent.SystemPrompt ?? string.Empty;

        // Inject Telegram chat ID if agent uses send_telegram tool and has a chat ID configured
        if (agent.ToolNames.Contains("send_telegram") && agent is AgentDefinition agentDef && agentDef.TelegramChatId.HasValue)
        {
            basePrompt = $"{basePrompt}\n\n**Telegram Chat ID**: When using the `send_telegram` tool, always use chat ID: `{agentDef.TelegramChatId}`. Do not ask the user for a chat ID—use this configured value.";
        }

        if (agent.SkillNames.Count == 0)
            return string.IsNullOrEmpty(basePrompt) ? null : basePrompt;

        var skillPrompts = new List<string>();
        foreach (var skillName in agent.SkillNames)
        {
            var skill = skillRegistry.Get(skillName);
            if (skill is not null && !string.IsNullOrEmpty(skill.PromptText))
                skillPrompts.Add($"## Skill: {skill.Name}\n{skill.PromptText}");
        }

        if (skillPrompts.Count == 0)
            return string.IsNullOrEmpty(basePrompt) ? null : basePrompt;

        return $"{basePrompt}\n\n---\n\n{string.Join("\n\n", skillPrompts)}";
    }
}
