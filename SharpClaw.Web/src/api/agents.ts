import { apiFetch } from './client';

export interface AgentSummary {
    name: string;
    description: string | null;
    llm: string | null;
    model: string | null;
    toolNames: string[];
    mcpNames: string[];
    skillNames: string[];
    subAgentNames: string[];
}

export interface AgentDetail extends AgentSummary {
    systemPrompt: string | null;
    rawContent: string | null;
}

export const getAgents = () => apiFetch<AgentSummary[]>('/agents');
export const getAgent = (name: string) => apiFetch<AgentDetail>(`/agents/${name}`);
export const updateAgent = (name: string, content: string) =>
    apiFetch<void>(`/agents/${name}`, { method: 'PUT', body: JSON.stringify({ content }) });
export const createAgent = (name: string, content: string) =>
    apiFetch<void>('/agents', { method: 'POST', body: JSON.stringify({ name, content }) });
export const deleteAgent = (name: string) =>
    apiFetch<void>(`/agents/${name}`, { method: 'DELETE' });
