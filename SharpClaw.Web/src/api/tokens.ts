import { apiFetch } from './client';

export interface TokenUsageSummary {
    agentName: string;
    provider: string;
    model: string | null;
    requestCount: number;
    totalInputTokens: number;
    totalOutputTokens: number;
    avgDurationMs: number;
}

export interface TokenUsageDaily {
    date: string;
    requestCount: number;
    totalInputTokens: number;
    totalOutputTokens: number;
}

export interface TokenUsageEntry {
    id: number;
    timestampUtc: string;
    agentName: string;
    provider: string;
    model: string | null;
    sessionId: string | null;
    inputTokens: number | null;
    outputTokens: number | null;
    totalTokens: number | null;
    durationMs: number | null;
    toolCount: number;
    mcpCount: number;
    skills: string | null;
    success: boolean;
}

export function getTokenSummary(params?: { from?: string; to?: string; agent?: string; provider?: string }): Promise<TokenUsageSummary[]> {
    const q = new URLSearchParams();
    if (params?.from) q.set('from', params.from);
    if (params?.to) q.set('to', params.to);
    if (params?.agent) q.set('agent', params.agent);
    if (params?.provider) q.set('provider', params.provider);
    const qs = q.toString();
    return apiFetch(`/tokens/summary${qs ? `?${qs}` : ''}`);
}

export function getTokenDaily(params?: { from?: string; to?: string; agent?: string; provider?: string }): Promise<TokenUsageDaily[]> {
    const q = new URLSearchParams();
    if (params?.from) q.set('from', params.from);
    if (params?.to) q.set('to', params.to);
    if (params?.agent) q.set('agent', params.agent);
    if (params?.provider) q.set('provider', params.provider);
    const qs = q.toString();
    return apiFetch(`/tokens/daily${qs ? `?${qs}` : ''}`);
}

export function getTokenRecent(params?: { limit?: number; agent?: string; provider?: string }): Promise<TokenUsageEntry[]> {
    const q = new URLSearchParams();
    if (params?.limit) q.set('limit', params.limit.toString());
    if (params?.agent) q.set('agent', params.agent);
    if (params?.provider) q.set('provider', params.provider);
    const qs = q.toString();
    return apiFetch(`/tokens/recent${qs ? `?${qs}` : ''}`);
}
