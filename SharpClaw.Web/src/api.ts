import type { Persona, Session, PersistedSession, AgentDefinition, AgentEvent, AgentUpsertRequest, BackendModelListResponse, McpDefinition, McpUpsertRequest } from './types';

const BASE = '/api';  // All API routes are under /api/

function headers(): HeadersInit {
    const h: HeadersInit = { 'Content-Type': 'application/json' };
    const key = localStorage.getItem('sharpclaw-api-key');
    if (key) h['X-Api-Key'] = key;
    return h;
}

export function setApiKey(key: string) {
    localStorage.setItem('sharpclaw-api-key', key);
}

export function clearApiKey() {
    localStorage.removeItem('sharpclaw-api-key');
}

export function hasApiKey(): boolean {
    return !!localStorage.getItem('sharpclaw-api-key');
}

/** Verify the stored key by hitting a lightweight endpoint. Returns true if valid. */
export async function checkAuth(): Promise<boolean> {
    const res = await fetch(`${BASE}/personas`, { headers: headers() });
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

async function readError(res: Response, fallback: string): Promise<string> {
    try {
        const body = await res.json() as { error?: string };
        return body.error ?? fallback;
    } catch {
        return fallback;
    }
}
