import { apiFetch } from './client';

export interface ScheduledTaskSummary {
    id: string;
    agent: string;
    description: string;
    cron: string;
    isOneOff: boolean;
    channelType: string;
    enabled: boolean;
    created: string;
    nextRun: string;
    lastRun: string | null;
    prompt: string;
}

export const getTasks = () => apiFetch<ScheduledTaskSummary[]>('/tasks');
