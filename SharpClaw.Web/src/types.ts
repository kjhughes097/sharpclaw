/* ── Types shared across the web UI ─────────────────────────────────────────── */

export interface Persona {
    file: string;
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
    file: string;
    name: string;
    description: string;
    backend: string;
    model: string;
    mcpServers: string[];
    permissionPolicy: Record<string, string>;
    systemPrompt: string;
    isEnabled: boolean;
    sessionCount: number;
}

export interface AgentUpsertRequest {
    file: string;
    name: string;
    description: string;
    backend: string;
    model: string;
    mcpServers: string[];
    permissionPolicy: Record<string, string>;
    systemPrompt: string;
    isEnabled: boolean;
}

export interface Session {
    sessionId: string;
    persona: string;
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

export type AgentEvent =
    | TokenEvent
    | ToolCallEvent
    | ToolResultEvent
    | PermissionRequestEvent
    | StatusEvent
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
