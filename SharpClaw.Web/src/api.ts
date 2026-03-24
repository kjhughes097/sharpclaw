import type { Persona, Session, AgentEvent } from './types';

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

export async function createSession(personaFile: string): Promise<Session> {
    const res = await fetch(`${BASE}/sessions`, {
        method: 'POST',
        headers: headers(),
        body: JSON.stringify({ persona: personaFile }),
    });
    if (!res.ok) throw new Error(`POST /sessions: ${res.status}`);
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
    const eventTypes = ['token', 'tool_call', 'tool_result', 'permission_request', 'status', 'done'] as const;

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
