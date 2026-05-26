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

export interface ScheduledTaskDetail {
    id: string;
    agent: string;
    description: string;
    cron: string;
    isOneOff: boolean;
    channelKey: string;
    channelType: string;
    enabled: boolean;
    created: string;
    nextRun: string;
    lastRun: string | null;
    prompt: string;
}

export interface TaskUpdateRequest {
    description?: string;
    cron?: string;
    prompt?: string;
    enabled?: boolean;
    isOneOff?: boolean;
    agent?: string;
}

export const getTasks = () => apiFetch<ScheduledTaskSummary[]>('/tasks');

export const getTask = (id: string) => apiFetch<ScheduledTaskDetail>(`/tasks/${id}`);

export const updateTask = (id: string, data: TaskUpdateRequest) =>
    apiFetch<{ id: string; message: string }>(`/tasks/${id}`, {
        method: 'PUT',
        body: JSON.stringify(data),
    });

export const deleteTask = (id: string) =>
    apiFetch<void>(`/tasks/${id}`, { method: 'DELETE' });
