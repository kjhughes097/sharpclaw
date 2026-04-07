import type { Persona, Session, PersistedSession, AgentDefinition, AgentEvent, AgentUpsertRequest, BackendModelListResponse, McpDefinition, McpUpsertRequest, TelegramSettings, TelegramWorkerToken, UpdateTelegramSettingsRequest, BackendSettings, UpdateBackendSettingsRequest, AppSettings, UpdateAppSettingsRequest, AuthStatus, SetupAuthRequest, LoginRequest, TokenUsageSummary, TokenUsageHistory } from './types';

const BASE = '/api';  // All API routes are under /api/

function headers(): HeadersInit {
    return { 'Content-Type': 'application/json' };
}

export async function fetchAuthStatus(): Promise<AuthStatus> {
    const res = await fetch(`${BASE}/auth/status`, { headers: headers() });
    if (!res.ok) throw new Error(await readError(res, `GET /auth/status: ${res.status}`));
    return res.json();
}

export async function setupAuth(payload: SetupAuthRequest): Promise<void> {
    const res = await fetch(`${BASE}/auth/setup`, {
        method: 'POST',
        headers: headers(),
        body: JSON.stringify(payload),
    });
    if (!res.ok) throw new Error(await readError(res, `POST /auth/setup: ${res.status}`));
}

export async function login(payload: LoginRequest): Promise<void> {
    const res = await fetch(`${BASE}/auth/login`, {
        method: 'POST',
        headers: headers(),
        body: JSON.stringify(payload),
    });
    if (!res.ok) throw new Error(await readError(res, `POST /auth/login: ${res.status}`));
}

export async function logout(): Promise<void> {
    await fetch(`${BASE}/auth/logout`, {
        method: 'POST',
        headers: headers(),
    });
}

/** Verify the current auth cookie by hitting a lightweight endpoint. */
export async function checkAuth(): Promise<boolean> {
    const res = await fetch(`${BASE}/auth/me`, { headers: headers() });
    return res.ok;
}

export async function fetchPersonas(): Promise<Persona[]> {
    const res = await fetch(`${BASE}/personas`, { headers: headers() });
    if (!res.ok) throw new Error(`GET /personas: ${res.status}`);
    return res.json();
}

export async function fetchAgents(): Promise<AgentDefinition[]> {
    const res = await fetch(`${BASE}/agents`, { headers: headers() });
    if (!res.ok) throw new Error(`GET /agents: ${res.status}`);
    return res.json();
}

export async function fetchBackendModels(backend: string): Promise<BackendModelListResponse> {
    const res = await fetch(`${BASE}/backends/${encodeURIComponent(backend)}/models`, { headers: headers() });
    if (!res.ok) throw new Error(await readError(res, `GET /backends/${backend}/models: ${res.status}`));
    return res.json();
}

export async function fetchBackendSettings(): Promise<BackendSettings[]> {
    const res = await fetch(`${BASE}/backends/settings`, { headers: headers() });
    if (!res.ok) throw new Error(await readError(res, `GET /backends/settings: ${res.status}`));
    return res.json();
}

export async function updateBackendSettings(backend: string, payload: UpdateBackendSettingsRequest): Promise<BackendSettings> {
    const res = await fetch(`${BASE}/backends/settings/${encodeURIComponent(backend)}`, {
        method: 'PUT',
        headers: headers(),
        body: JSON.stringify(payload),
    });
    if (!res.ok) throw new Error(await readError(res, `PUT /backends/settings/${backend}: ${res.status}`));
    return res.json();
}

export async function fetchAppSettings(): Promise<AppSettings> {
    const res = await fetch(`${BASE}/settings/app`, { headers: headers() });
    if (!res.ok) throw new Error(await readError(res, `GET /settings/app: ${res.status}`));
    return res.json();
}

export async function updateAppSettings(payload: UpdateAppSettingsRequest): Promise<AppSettings> {
    const res = await fetch(`${BASE}/settings/app`, {
        method: 'PUT',
        headers: headers(),
        body: JSON.stringify(payload),
    });
    if (!res.ok) throw new Error(await readError(res, `PUT /settings/app: ${res.status}`));
    return res.json();
}

export async function fetchMcps(): Promise<McpDefinition[]> {
    const res = await fetch(`${BASE}/mcps`, { headers: headers() });
    if (!res.ok) throw new Error(`GET /mcps: ${res.status}`);
    return res.json();
}

export async function createAgent(agent: AgentUpsertRequest): Promise<AgentDefinition> {
    const res = await fetch(`${BASE}/agents`, {
        method: 'POST',
        headers: headers(),
        body: JSON.stringify(agent),
    });
    if (!res.ok) throw new Error(await readError(res, `POST /agents: ${res.status}`));
    return res.json();
}

export async function updateAgent(id: string, agent: AgentUpsertRequest): Promise<AgentDefinition> {
    const res = await fetch(`${BASE}/agents/${encodeURIComponent(id)}`, {
        method: 'PUT',
        headers: headers(),
        body: JSON.stringify(agent),
    });
    if (!res.ok) throw new Error(await readError(res, `PUT /agents/${id}: ${res.status}`));
    return res.json();
}

export async function setAgentEnabled(id: string, isEnabled: boolean): Promise<void> {
    const res = await fetch(`${BASE}/agents/${encodeURIComponent(id)}/enabled`, {
        method: 'PATCH',
        headers: headers(),
        body: JSON.stringify({ isEnabled }),
    });
    if (!res.ok) throw new Error(await readError(res, `PATCH /agents/${id}/enabled: ${res.status}`));
}

export async function deleteAgent(id: string, purgeSessions = false): Promise<{ id: string; deletedSessions: number }> {
    const suffix = purgeSessions ? '?purgeSessions=true' : '';
    const res = await fetch(`${BASE}/agents/${encodeURIComponent(id)}${suffix}`, {
        method: 'DELETE',
        headers: headers(),
    });
    if (!res.ok) throw new Error(await readError(res, `DELETE /agents/${id}: ${res.status}`));
    return res.json();
}

export async function createMcp(mcp: McpUpsertRequest): Promise<McpDefinition> {
    const res = await fetch(`${BASE}/mcps`, {
        method: 'POST',
        headers: headers(),
        body: JSON.stringify(mcp),
    });
    if (!res.ok) throw new Error(await readError(res, `POST /mcps: ${res.status}`));
    return res.json();
}

export async function updateMcp(slug: string, mcp: McpUpsertRequest): Promise<McpDefinition> {
    const res = await fetch(`${BASE}/mcps/${encodeURIComponent(slug)}`, {
        method: 'PUT',
        headers: headers(),
        body: JSON.stringify(mcp),
    });
    if (!res.ok) throw new Error(await readError(res, `PUT /mcps/${slug}: ${res.status}`));
    return res.json();
}

export async function setMcpEnabled(slug: string, isEnabled: boolean): Promise<void> {
    const res = await fetch(`${BASE}/mcps/${encodeURIComponent(slug)}/enabled`, {
        method: 'PATCH',
        headers: headers(),
        body: JSON.stringify({ isEnabled }),
    });
    if (!res.ok) throw new Error(await readError(res, `PATCH /mcps/${slug}/enabled: ${res.status}`));
}

export async function deleteMcp(slug: string, detachAgents = false): Promise<{ slug: string; detachedAgents: number }> {
    const suffix = detachAgents ? '?detachAgents=true' : '';
    const res = await fetch(`${BASE}/mcps/${encodeURIComponent(slug)}${suffix}`, {
        method: 'DELETE',
        headers: headers(),
    });
    if (!res.ok) throw new Error(await readError(res, `DELETE /mcps/${slug}: ${res.status}`));
    return res.json();
}

export async function fetchTelegramSettings(): Promise<TelegramSettings> {
    const res = await fetch(`${BASE}/integrations/telegram`, { headers: headers() });
    if (!res.ok) throw new Error(await readError(res, `GET /integrations/telegram: ${res.status}`));
    return res.json();
}

export async function updateTelegramSettings(payload: UpdateTelegramSettingsRequest): Promise<TelegramSettings> {
    const res = await fetch(`${BASE}/integrations/telegram`, {
        method: 'PUT',
        headers: headers(),
        body: JSON.stringify(payload),
    });
    if (!res.ok) throw new Error(await readError(res, `PUT /integrations/telegram: ${res.status}`));
    return res.json();
}

export async function createTelegramWorkerToken(): Promise<TelegramWorkerToken> {
    const res = await fetch(`${BASE}/integrations/telegram/worker-token`, {
        method: 'POST',
        headers: headers(),
    });
    if (!res.ok) throw new Error(await readError(res, `POST /integrations/telegram/worker-token: ${res.status}`));
    return res.json();
}

export async function createSession(agentId: string): Promise<Session> {
    const res = await fetch(`${BASE}/sessions`, {
        method: 'POST',
        headers: headers(),
        body: JSON.stringify({ agentId }),
    });
    if (!res.ok) throw new Error(`POST /sessions: ${res.status}`);
    return res.json();
}

export async function fetchSessions(): Promise<PersistedSession[]> {
    const res = await fetch(`${BASE}/sessions`, { headers: headers() });
    if (!res.ok) throw new Error(`GET /sessions: ${res.status}`);
    return res.json();
}

export async function fetchSession(sessionId: string): Promise<PersistedSession> {
    const res = await fetch(`${BASE}/sessions/${encodeURIComponent(sessionId)}`, { headers: headers() });
    if (!res.ok) throw new Error(`GET /sessions/${sessionId}: ${res.status}`);
    return res.json();
}

export async function deleteSession(sessionId: string): Promise<{ sessionId: string; deleted: boolean }> {
    const res = await fetch(`${BASE}/sessions/${encodeURIComponent(sessionId)}`, {
        method: 'DELETE',
        headers: headers(),
    });
    if (!res.ok) throw new Error(await readError(res, `DELETE /sessions/${sessionId}: ${res.status}`));
    return res.json();
}

export async function sendMessage(
    sessionId: string,
    message: string,
): Promise<{ sessionId: string; messageId: string }> {
    const res = await fetch(`${BASE}/sessions/${sessionId}/messages`, {
        method: 'POST',
        headers: headers(),
        body: JSON.stringify({ message }),
    });
    if (!res.ok) throw new Error(`POST /sessions/${sessionId}/messages: ${res.status}`);
    return res.json();
}

export function streamEvents(
    sessionId: string,
    messageId: string,
    onEvent: (event: AgentEvent) => void,
    onDone: () => void,
    onError: (err: Error) => void,
): () => void {
    const url = `${BASE}/sessions/${sessionId}/messages/${messageId}/stream`;
    const eventSource = new EventSource(url);
    const eventTypes = ['token', 'tool_call', 'tool_result', 'permission_request', 'status', 'usage', 'done'] as const;

    for (const type of eventTypes) {
        eventSource.addEventListener(type, (e: MessageEvent) => {
            try {
                const parsed = JSON.parse(e.data) as AgentEvent;
                onEvent(parsed);
                if (type === 'done') {
                    eventSource.close();
                    onDone();
                }
            } catch (err) {
                onError(err instanceof Error ? err : new Error(String(err)));
            }
        });
    }

    eventSource.onerror = () => {
        eventSource.close();
        onDone();
    };

    return () => eventSource.close();
}

export async function resolvePermission(
    sessionId: string,
    requestId: string,
    allow: boolean,
): Promise<void> {
    const res = await fetch(`${BASE}/sessions/${sessionId}/permissions/${requestId}`, {
        method: 'POST',
        headers: headers(),
        body: JSON.stringify({ allow }),
    });
    if (!res.ok) throw new Error(`POST permissions: ${res.status}`);
}

export async function fetchTokenUsageSummary(): Promise<TokenUsageSummary> {
    const res = await fetch(`${BASE}/token-usage/summary`, { headers: headers() });
    if (!res.ok) throw new Error(await readError(res, `GET /token-usage/summary: ${res.status}`));
    return res.json();
}

export async function fetchTokenUsageHistory(period: string = 'week'): Promise<TokenUsageHistory> {
    const res = await fetch(`${BASE}/token-usage/history?period=${encodeURIComponent(period)}`, { headers: headers() });
    if (!res.ok) throw new Error(await readError(res, `GET /token-usage/history: ${res.status}`));
    return res.json();
}

async function readError(res: Response, fallback: string): Promise<string> {
    try {
        const body = await res.json() as { error?: string };
        return body.error ?? fallback;
    } catch {
        return fallback;
    }
}
