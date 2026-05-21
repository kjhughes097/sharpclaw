import { apiFetch } from './client';

export interface McpSummary {
    name: string;
    transport: string;
    command: string | null;
    url: string | null;
}

export const getMcps = () => apiFetch<McpSummary[]>('/mcps');
export const getMcpRaw = (name: string) =>
    fetch(`/api/mcps/${name}`).then(r => { if (!r.ok) throw new Error(`${r.status}`); return r.text(); });
export const updateMcp = (name: string, json: string) =>
    fetch(`/api/mcps/${name}`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: json })
        .then(r => { if (!r.ok) throw new Error(`${r.status}`); });
export const createMcp = (name: string, config: object) =>
    apiFetch<void>('/mcps', { method: 'POST', body: JSON.stringify({ name, config }) });
export const deleteMcp = (name: string) =>
    apiFetch<void>(`/mcps/${name}`, { method: 'DELETE' });
