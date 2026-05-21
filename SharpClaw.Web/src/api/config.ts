import { apiFetch } from './client';

export interface ConfigData {
    sharpClaw: {
        workspacePath: string;
        defaultAgent: string;
        chatHistoryLimit: number;
    };
    anthropic: {
        apiKey: string | null;
        defaultModel: string;
        maxTokens: number;
        isConfigured: boolean;
    };
    telegram: {
        botToken: string | null;
        allowedUsers: string[];
        defaultAgent: string;
        isConfigured: boolean;
    };
    openTelemetry: {
        endpoint: string;
    };
    anthropicAdminMcp: {
        apiKey: string | null;
        monthlyBudgetUsd: number;
        defaultLookbackDays: number;
        isConfigured: boolean;
    };
}

export const getConfig = () => apiFetch<ConfigData>('/config');
export const updateConfig = (data: object) =>
    apiFetch<void>('/config', { method: 'PUT', body: JSON.stringify(data) });
