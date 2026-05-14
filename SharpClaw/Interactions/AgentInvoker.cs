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
        // Try commands first (no LLM call)
        var cmdContext = new CommandContext(session.SessionId, prompt, session.AgentId);
        var cmdResult = await commandRouter.TryExecuteAsync(cmdContext, ct);

        if (cmdResult is not null)
        {
            if (cmdResult.SwitchedToAgent is not null)
                session.SetAgent(cmdResult.SwitchedToAgent);

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

        // Audit the request
        await auditService.LogAsync(session.AgentId, AuditEntryType.Request, prompt, ct);

        // Set scheduling context so tools can access channel info
        schedulingContextAccessor.Current = channelContext;

        // Build system prompt with skills injected
        var systemPrompt = BuildSystemPromptWithSkills(agent);

        // First turn: create session; subsequent: reuse
        if (session.LlmSession is null)
        {
            var request = new AgentRunRequest(
                Prompt: prompt,
                Llm: agent.Llm,
                Model: agent.Model,
                SystemPromptOverride: systemPrompt,
                McpServerNames: agent.McpNames.Count > 0 ? agent.McpNames : null,
                ToolNames: agent.ToolNames.Count > 0 ? agent.ToolNames : null
            );
            var llmSession = await runner.CreateSessionAsync(request, ct);
            session.SetLlmSession(llmSession);
        }

        var result = await runner.SendAsync(session.LlmSession!, prompt, agent.Llm, ct);

        var responseText = result.Success
            ? result.Response
            : $"[Agent error: {result.Error}]";

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
