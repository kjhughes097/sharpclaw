/* ── Types shared across the web UI ─────────────────────────────────────────── */

export interface Persona {
    id: string;
    name: string;
    description: string;
    backend: string;
    model: string;
    mcpServers: string[];
    permissionPolicy: Record<string, string>;
    systemPrompt: string;
    isEnabled: boolean;
}

export interface AgentDefinition {
    id: string;
    name: string;
    description: string;
    backend: string;
    model: string;
    mcpServers: string[];
    permissionPolicy: Record<string, string>;
    systemPrompt: string;
    isEnabled: boolean;
    sessionCount: number;
    dailyTokenLimit: number | null;
}

export interface AgentUpsertRequest {
    name: string;
    description: string;
    backend: string;
    model: string;
    mcpServers: string[];
    permissionPolicy: Record<string, string>;
    systemPrompt: string;
    isEnabled: boolean;
    dailyTokenLimit?: number | null;
}

export interface BackendModelOption {
    id: string;
    displayName: string;
}

export interface BackendModelListResponse {
    models: BackendModelOption[];
    source: 'live' | 'cache';
    cachedAt: string | null;
    warning?: string | null;
}

export interface BackendSettings {
    backend: string;
    isEnabled: boolean;
    hasApiKey: boolean;
    maskedApiKey: string | null;
    requiresApiKey: boolean;
    updatedAt: string | null;
    dailyTokenLimit: number;
}

export interface UpdateBackendSettingsRequest {
    isEnabled: boolean;
    apiKey?: string;
    clearApiKey?: boolean;
    dailyTokenLimit?: number;
}

export interface AppSettings {
    workspacePath: string;
}

export interface UpdateAppSettingsRequest {
    workspacePath?: string;
    clearWorkspacePath?: boolean;
}

export interface AuthStatus {
    isConfigured: boolean;
}

export interface AuthUser {
    username: string;
}

export interface SetupAuthRequest {
    username: string;
    password: string;
    confirmPassword: string;
}

export interface LoginRequest {
    username: string;
    password: string;
}

export interface McpDefinition {
    slug: string;
    name: string;
    description: string;
    command: string;
    args: string[];
    isEnabled: boolean;
    linkedAgentCount: number;
}

export interface McpUpsertRequest {
    slug: string;
    name: string;
    description: string;
    command: string;
    args: string[];
    isEnabled: boolean;
}

export interface TelegramSettings {
    isEnabled: boolean;
    hasBotToken: boolean;
    maskedBotToken: string | null;
    allowedUserIds: number[];
    allowedUsernames: string[];
    mappingStorePath: string;
}

export interface TelegramWorkerToken {
    token: string;
    expiresAt: string;
}

export interface UpdateTelegramSettingsRequest {
    isEnabled: boolean;
    botToken?: string;
    clearBotToken?: boolean;
    allowedUserIds: number[];
    allowedUsernames: string[];
    mappingStorePath?: string;
    clearMappingStorePath?: boolean;
}

export interface Session {
    sessionId: string;
    persona: string;
}

export interface PersistedSession {
    sessionId: string;
    persona: string;
    agentId: string;
    createdAt: string;
    lastActivityAt: string;
    messages: ChatMessage[];
    eventLogs: PersistedStreamItem[][];
}

export interface ChatMessage {
    role: 'user' | 'assistant';
    content: string;
}

/* ── SSE event payloads (mirror SharpClaw.Core/AgentEvent.cs) ─────────────── */

export interface TokenEvent {
    type: 'token';
    text: string;
}

export interface ToolCallEvent {
    type: 'tool_call';
    tool: string;
    input: Record<string, unknown> | null;
}

export interface ToolResultEvent {
    type: 'tool_result';
    tool: string;
    result: string;
    isError: boolean;
}

export interface PermissionRequestEvent {
    type: 'permission_request';
    tool: string;
    input: Record<string, unknown> | null;
    requestId: string;
}

export interface DoneEvent {
    type: 'done';
    content: string;
}

export interface StatusEvent {
    type: 'status';
    message: string;
}

export interface UsageEvent {
    type: 'usage';
    provider: string;
    inputTokens: number;
    outputTokens: number;
    totalTokens: number;
}

export type AgentEvent =
    | TokenEvent
    | ToolCallEvent
    | ToolResultEvent
    | PermissionRequestEvent
    | StatusEvent
    | UsageEvent
    | DoneEvent;

/* ── Stream item: an AgentEvent plus metadata for the UI ──────────────────── */

export interface StreamItem {
    id: string;
    event: AgentEvent;
    /** For tool_call events, paired result once it arrives */
    result?: ToolResultEvent;
    /** For permission_request events, whether user has responded */
    resolved?: boolean;
}

export interface PersistedStreamItem {
    event: AgentEvent;
    result?: ToolResultEvent;
}

/* ── Token usage types ────────────────────────────────────────────────────── */

export interface ProviderDailyUsage {
    provider: string;
    totalTokens: number;
    dailyLimit: number;
    usagePercent: number;
}

export interface AgentDailyUsage {
    agentSlug: string;
    totalTokens: number;
    dailyLimit: number | null;
    usagePercent: number | null;
}

export interface TokenUsageSummary {
    providers: ProviderDailyUsage[];
    agents: AgentDailyUsage[];
}

export interface TokenUsageDataPoint {
    bucket: string;
    agentSlug: string;
    totalTokens: number;
}

export interface TokenUsageHistory {
    period: string;
    dataPoints: TokenUsageDataPoint[];
}
