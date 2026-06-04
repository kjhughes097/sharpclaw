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
    taskType: string;
    command: string | null;
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
    taskType: string;
    command: string | null;
}

export interface TaskUpdateRequest {
    description?: string;
    cron?: string;
    prompt?: string;
    enabled?: boolean;
    isOneOff?: boolean;
    agent?: string;
    command?: string;
}

export interface TaskCreateRequest {
    cron: string;
    taskType: string;
    agent?: string;
    command?: string;
    prompt?: string;
    description?: string;
    isOneOff?: boolean;
    enabled?: boolean;
}

export const getTasks = () => apiFetch<ScheduledTaskSummary[]>('/tasks');

export const getTask = (id: string) => apiFetch<ScheduledTaskDetail>(`/tasks/${id}`);

export const createTask = (data: TaskCreateRequest) =>
    apiFetch<{ id: string; message: string }>('/tasks', {
        method: 'POST',
        body: JSON.stringify(data),
    });

export const updateTask = (id: string, data: TaskUpdateRequest) =>
    apiFetch<{ id: string; message: string }>(`/tasks/${id}`, {
        method: 'PUT',
        body: JSON.stringify(data),
    });

export const deleteTask = (id: string) =>
    apiFetch<void>(`/tasks/${id}`, { method: 'DELETE' });
