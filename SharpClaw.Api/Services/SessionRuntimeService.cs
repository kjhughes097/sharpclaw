using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SharpClaw.Api.Models;
using SharpClaw.Core;

namespace SharpClaw.Api.Services;

public sealed class SessionRuntimeService(
    SessionStore store,
    BackendRegistry backendRegistry,
    BackendSettingsService backendSettingsService,
    ILogger<SessionRuntimeService> logger)
{
    private readonly ConcurrentDictionary<string, AgentRunner> _runners = new();
    private readonly ConcurrentDictionary<string, Channel<AgentEvent>> _messageStreams = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public bool TryGetStream(string sessionId, string messageId, out Channel<AgentEvent> channel)
        => _messageStreams.TryGetValue($"{sessionId}/{messageId}", out channel!);

    public bool HasActiveStreams(string sessionId)
        => _messageStreams.Keys.Any(key => key.StartsWith($"{sessionId}/", StringComparison.Ordinal));

    public async Task CleanupSessionAsync(string sessionId)
    {
        if (_runners.TryRemove(sessionId, out var runner))
            await runner.DisposeAsync();

        foreach (var streamKey in _messageStreams.Keys.Where(key => key.StartsWith($"{sessionId}/", StringComparison.Ordinal)).ToList())
        {
            if (_messageStreams.TryRemove(streamKey, out var stream))
                stream.Writer.TryComplete();
        }
    }

    public async Task<ApiResponse<IApiPayload>> CreateSessionAsync(string? agentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return new ApiResponse<IApiPayload>(StatusCodes.Status400BadRequest, new ErrorResponse("agentId is required."));

        var normalizedAgentId = agentId.Trim();
        var agentRecord = store.GetAgent(normalizedAgentId);
        if (agentRecord is null)
            return new ApiResponse<IApiPayload>(StatusCodes.Status404NotFound, new ErrorResponse($"Agent '{normalizedAgentId}' not found."));
        if (!agentRecord.IsEnabled)
            return new ApiResponse<IApiPayload>(StatusCodes.Status409Conflict, new ErrorResponse($"Agent '{normalizedAgentId}' is disabled."));

        var sessionId = Guid.NewGuid().ToString("N")[..12];
        store.CreateSession(sessionId, agentRecord.Slug);

        try
        {
            var runner = CreateRunner(agentRecord);
            await runner.InitializeAsync(ct);
            _runners[sessionId] = runner;
        }
        catch (InvalidOperationException ex)
        {
            store.DeleteSession(sessionId);
            return new ApiResponse<IApiPayload>(StatusCodes.Status409Conflict, new ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize session runner for agent '{AgentId}'", agentRecord.Slug);
            store.DeleteSession(sessionId);
            return new ApiResponse<IApiPayload>(
                StatusCodes.Status500InternalServerError,
                new ErrorResponse($"Failed to initialize agent '{agentRecord.Slug}'. Check MCP configuration and runtime dependencies."));
        }

        return new ApiResponse<IApiPayload>(
            StatusCodes.Status200OK,
            new SessionCreatedResponse(sessionId, agentRecord.Name, agentRecord.Slug));
    }

    public async Task<ApiResponse<IApiPayload>> DeleteSessionAsync(string sessionId)
    {
        var session = store.ListSessions().FirstOrDefault(item => item.SessionId == sessionId);
        if (session is null)
            return new ApiResponse<IApiPayload>(StatusCodes.Status404NotFound, new ErrorResponse($"Session '{sessionId}' not found."));

        if (HasActiveStreams(sessionId))
        {
            return new ApiResponse<IApiPayload>(
                StatusCodes.Status409Conflict,
                new ErrorResponse($"Session '{sessionId}' is currently streaming and cannot be deleted yet."));
        }

        if (!store.DeleteSession(sessionId))
            return new ApiResponse<IApiPayload>(StatusCodes.Status404NotFound, new ErrorResponse($"Session '{sessionId}' not found."));

        await CleanupSessionAsync(sessionId);

        return new ApiResponse<IApiPayload>(StatusCodes.Status200OK, new SessionDeletedResponse(sessionId, true));
    }

    public ApiResponse<IApiPayload> ResolvePermission(string sessionId, string requestId, bool allow)
    {
        if (!_runners.TryGetValue(sessionId, out var runner))
            return new ApiResponse<IApiPayload>(StatusCodes.Status404NotFound, new ErrorResponse($"Session '{sessionId}' not found."));

        var gate = runner.PermissionGate;
        if (gate is null)
            return new ApiResponse<IApiPayload>(StatusCodes.Status500InternalServerError, new ErrorResponse("Permission gate is unavailable."));

        var resolved = gate.Resolve(requestId, allow);
        if (!resolved)
            return new ApiResponse<IApiPayload>(StatusCodes.Status404NotFound, new ErrorResponse($"No pending permission request '{requestId}'."));

        return new ApiResponse<IApiPayload>(StatusCodes.Status200OK, new PermissionResolvedResponse(requestId, allow));
    }

    public async Task<ApiResponse<IApiPayload>> QueueMessageAsync(string sessionId, string? message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message))
            return new ApiResponse<IApiPayload>(StatusCodes.Status400BadRequest, new ErrorResponse("message is required."));

        var conversation = store.Load(sessionId);
        if (conversation is null)
            return new ApiResponse<IApiPayload>(StatusCodes.Status404NotFound, new ErrorResponse($"Session '{sessionId}' not found."));

        var isAdeSession = conversation.AgentSlug.Equals(ApiMapper.AdeAgentId, StringComparison.OrdinalIgnoreCase);

        AgentRunner? runner = null;
        if (!isAdeSession && !_runners.TryGetValue(sessionId, out runner))
        {
            var agentRecord = store.GetAgent(conversation.AgentSlug);
            if (agentRecord is null)
            {
                return new ApiResponse<IApiPayload>(
                    StatusCodes.Status404NotFound,
                    new ErrorResponse($"Agent definition '{conversation.AgentSlug}' not found."));
            }

            runner = CreateRunner(agentRecord);
            try
            {
                await runner.InitializeAsync(ct);
            }
            catch (InvalidOperationException ex)
            {
                return new ApiResponse<IApiPayload>(StatusCodes.Status409Conflict, new ErrorResponse(ex.Message));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to initialize session runner for agent '{AgentId}'", agentRecord.Slug);
                return new ApiResponse<IApiPayload>(
                    StatusCodes.Status500InternalServerError,
                    new ErrorResponse($"Failed to initialize agent '{agentRecord.Slug}'. Check MCP configuration and runtime dependencies."));
            }

            _runners[sessionId] = runner;
        }

        var userMsg = new ChatMessage(ChatRole.User, message);
        conversation.AddUser(message);
        store.Append(sessionId, userMsg);

        var messageId = Guid.NewGuid().ToString("N")[..8];
        var streamKey = $"{sessionId}/{messageId}";
        var channel = Channel.CreateUnbounded<AgentEvent>();
        _messageStreams[streamKey] = channel;

        _ = Task.Run(() => RunMessageTurnAsync(sessionId, messageId, message, conversation, runner, isAdeSession, channel));

        return new ApiResponse<IApiPayload>(StatusCodes.Status200OK, new MessageQueuedResponse(sessionId, messageId));
    }

    public async Task StreamEventsAsync(string sessionId, string messageId, HttpContext httpContext)
    {
        if (!TryGetStream(sessionId, messageId, out var channel))
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new ErrorResponse("Stream not found."));
            return;
        }

        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";

        var ct = httpContext.RequestAborted;
        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
            {
                var data = JsonSerializer.Serialize(evt, evt.GetType(), _jsonOptions);
                await httpContext.Response.WriteAsync($"event: {evt.Type}\ndata: {data}\n\n", ct);
                await httpContext.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _messageStreams.TryRemove($"{sessionId}/{messageId}", out _);
        }
    }

    private AgentRunner CreateRunner(AgentRecord agentRecord)
    {
        var persona = agentRecord.ToPersona();
        var mcps = store.ListMcpsBySlug(agentRecord.McpServers, includeDisabled: false);
        return new AgentRunner(persona, mcps, CreateBackend, store.GetWorkspacePath(), logger: logger);
    }

    private IAgentBackend CreateBackend(AgentPersona persona, PermissionGate gate)
    {
        backendSettingsService.EnsureBackendConfigured(persona.Backend);

        if (!backendRegistry.TryGet(persona.Backend, out var provider))
            throw new InvalidOperationException($"Unknown backend '{persona.Backend}'.");

        return provider.CreateBackend(persona, gate);
    }

    private async Task RunMessageTurnAsync(
        string sessionId,
        string messageId,
        string message,
        ConversationHistory conversation,
        AgentRunner? runner,
        bool isAdeSession,
        Channel<AgentEvent> channel)
    {
        AgentRunner? turnRunner = runner;
        var disposeTurnRunner = false;

        try
        {
            var assistantIndex = conversation.Messages.Count(item => item.Role == ChatRole.Assistant);
            var persistedEventLog = new List<StoredEventLogItem>();

            void RecordEvent(AgentEvent evt)
            {
                switch (evt)
                {
                    case ToolCallEvent toolCall:
                        persistedEventLog.Add(new StoredEventLogItem(toolCall, null));
                        break;
                    case ToolResultEvent toolResult:
                        for (var i = persistedEventLog.Count - 1; i >= 0; i--)
                        {
                            var item = persistedEventLog[i];
                            if (item.Event is ToolCallEvent pendingCall &&
                                pendingCall.Tool == toolResult.Tool &&
                                item.Result is null)
                            {
                                persistedEventLog[i] = item with { Result = toolResult };
                                return;
                            }
                        }

                        persistedEventLog.Add(new StoredEventLogItem(toolResult, toolResult));
                        break;
                    case StatusEvent status:
                        persistedEventLog.Add(new StoredEventLogItem(status, null));
                        break;
                    case UsageEvent usage:
                        persistedEventLog.Add(new StoredEventLogItem(usage, null));
                        break;
                    case PermissionRequestEvent permissionRequest:
                        persistedEventLog.Add(new StoredEventLogItem(permissionRequest, null));
                        break;
                }
            }

            void EmitStatus(string statusMessage)
            {
                var status = new StatusEvent(statusMessage);
                RecordEvent(status);
                channel.Writer.TryWrite(status);
            }

            if (isAdeSession)
            {
                var adeRecord = store.GetAgent(conversation.AgentSlug);
                if (adeRecord is null)
                    throw new InvalidOperationException($"Agent definition '{conversation.AgentSlug}' not found.");

                if (_runners.TryRemove(sessionId, out var staleRunner))
                    await staleRunner.DisposeAsync();

                var availableAgents = new Dictionary<string, string>();
                foreach (var agentRec in store.ListAgents())
                {
                    if (agentRec.Slug.Equals(ApiMapper.AdeAgentId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!agentRec.IsEnabled)
                        continue;

                    availableAgents[agentRec.Slug] = agentRec.Name + " — " + agentRec.Description;
                }

                EmitStatus("Checking who can handle this...");
                logger.LogInformation(
                    "Ade routing started for session {SessionId}, message {MessageId}.",
                    sessionId,
                    messageId);

                var adePersona = adeRecord.ToPersona();

                var routingAgent = new RoutingAgent(
                    CreateBackend(adePersona, new PermissionGate(adePersona.PermissionPolicy)),
                    adePersona);

                var decision = await routingAgent.RouteAsync(message, availableAgents);

                if (decision.Agent is not null)
                {
                    var specialistRecord = store.GetAgent(decision.Agent);
                    if (specialistRecord is not null)
                    {
                        EmitStatus($"Passing this to {specialistRecord.Name}...");
                        logger.LogInformation(
                            "Ade routed session {SessionId}, message {MessageId} to specialist {AgentSlug} ({AgentName}).",
                            sessionId,
                            messageId,
                            specialistRecord.Slug,
                            specialistRecord.Name);

                        var specialistRunner = CreateRunner(specialistRecord);
                        await specialistRunner.InitializeAsync(CancellationToken.None);
                        turnRunner = specialistRunner;
                        disposeTurnRunner = true;
                        _runners[sessionId] = turnRunner;

                        if (decision.RewrittenPrompt is not null)
                            conversation.ReplaceLastUser(decision.RewrittenPrompt);
                    }
                }
                else
                {
                    EmitStatus("I'll handle this myself...");
                    logger.LogInformation(
                        "Ade kept session {SessionId}, message {MessageId} for direct handling.",
                        sessionId,
                        messageId);

                    var directAdeRunner = CreateRunner(adeRecord with
                    {
                        SystemPrompt = ApiMapper.BuildDirectResponseSystemPrompt(adeRecord),
                    });
                    await directAdeRunner.InitializeAsync(CancellationToken.None);
                    turnRunner = directAdeRunner;
                    disposeTurnRunner = true;
                    _runners[sessionId] = turnRunner;
                }

                logger.LogInformation(
                    "Ade handling phase started for session {SessionId}, message {MessageId} with runner persona {PersonaName}.",
                    sessionId,
                    messageId,
                    turnRunner?.Persona.Name);
            }

            var fullText = new StringBuilder();
            await foreach (var evt in turnRunner!.StreamAsync(
                conversation.Messages,
                eventSink: e =>
                {
                    RecordEvent(e);
                    channel.Writer.TryWrite(e);
                },
                ct: CancellationToken.None))
            {
                RecordEvent(evt);
                channel.Writer.TryWrite(evt);
                if (evt is DoneEvent done)
                    fullText.Append(done.Content);
            }

            if (fullText.Length > 0)
            {
                var assistantMsg = new ChatMessage(ChatRole.Assistant, fullText.ToString());
                conversation.AddAssistant(fullText.ToString());
                store.Append(sessionId, assistantMsg);
                store.SaveEventLog(sessionId, assistantIndex, persistedEventLog);
                logger.LogInformation(
                    "Assistant response persisted for session {SessionId}, message {MessageId}, length {Length}.",
                    sessionId,
                    messageId,
                    fullText.Length);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Message handling failed for session {SessionId}, message {MessageId}.", sessionId, messageId);
            channel.Writer.TryWrite(new DoneEvent($"Error: {ex.Message}"));
        }
        finally
        {
            if (disposeTurnRunner && turnRunner is not null)
            {
                if (_runners.TryGetValue(sessionId, out var currentRunner) && ReferenceEquals(currentRunner, turnRunner))
                    _runners.TryRemove(sessionId, out _);

                await turnRunner.DisposeAsync();
            }

            channel.Writer.TryComplete();
        }
    }
}