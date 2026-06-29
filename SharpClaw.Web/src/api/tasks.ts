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
    channelType?: string;
    channelKey?: string;
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
    channelType?: string;
    channelKey?: string;
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

// -- Comments --

export interface TaskComment {
    id: string;
    taskId: string;
    author: string;
    content: string;
    created: string;
    updated: string | null;
}

export const getTaskComments = (taskId: string) =>
    apiFetch<TaskComment[]>(`/tasks/${taskId}/comments`);

export const createTaskComment = (taskId: string, content: string, author: string) =>
    apiFetch<TaskComment>(`/tasks/${taskId}/comments`, {
        method: 'POST',
        body: JSON.stringify({ author, content }),
    });

export const updateTaskComment = (taskId: string, commentId: string, content: string, author: string) =>
    apiFetch<TaskComment>(`/tasks/${taskId}/comments/${commentId}`, {
        method: 'PUT',
        body: JSON.stringify({ author, content }),
    });

export const deleteTaskComment = (taskId: string, commentId: string, author: string) =>
    apiFetch<void>(`/tasks/${taskId}/comments/${commentId}?author=${encodeURIComponent(author)}`, {
        method: 'DELETE',
    });
