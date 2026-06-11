import { apiFetch } from './client';

export interface ProjectSummary {
    id: string;
    title: string;
    description: string | null;
    createdAt: string;
    ticketCount: number;
}

export interface TicketSummary {
    id: string;
    projectId: string;
    title: string;
    description: string | null;
    status: string;
    labels: string[];
    reporter: string | null;
    assignee: string | null;
    createdAt: string;
    updatedAt: string;
}

export interface User {
    id: string;
    name: string;
    type: 'agent' | 'human';
}

export const getProjects = () => apiFetch<ProjectSummary[]>('/projects');

export const createProject = (title: string, description?: string) =>
    apiFetch<{ id: string; title: string; description: string | null; createdAt: string }>('/projects', {
        method: 'POST',
        body: JSON.stringify({ title, description }),
    });

export const deleteProject = (projectId: string) =>
    apiFetch<void>(`/projects/${projectId}`, { method: 'DELETE' });

export const getProjectTickets = async (projectId: string): Promise<TicketSummary[]> => {
    const tickets = await apiFetch<TicketSummary[]>(`/projects/${projectId}/tickets`);
    return tickets.map(t => ({ ...t, labels: t.labels ?? [], reporter: t.reporter ?? null, assignee: t.assignee ?? null }));
};

export const getUsers = () => apiFetch<User[]>('/users');

export const getLabels = () => apiFetch<string[]>('/labels');

export const addLabel = (name: string) =>
    apiFetch<string[]>('/labels', { method: 'POST', body: JSON.stringify({ name }) });

export const removeLabel = (name: string) =>
    apiFetch<void>(`/labels/${encodeURIComponent(name)}`, { method: 'DELETE' });

export const createTicket = (projectId: string, data: { title: string; description?: string; reporter?: string; assignee?: string; labels?: string[] }) =>
    apiFetch<TicketSummary>(`/projects/${projectId}/tickets`, {
        method: 'POST',
        body: JSON.stringify(data),
    });

export const updateTicket = (projectId: string, ticketId: string, data: { title?: string; description?: string; status?: string; assignee?: string; labels?: string[] }) =>
    apiFetch<TicketSummary>(`/projects/${projectId}/tickets/${ticketId}`, {
        method: 'PATCH',
        body: JSON.stringify(data),
    });

export const updateTicketStatus = (projectId: string, ticketId: string, status: string) =>
    updateTicket(projectId, ticketId, { status });

export const improveTicket = (projectId: string, ticketId: string) =>
    apiFetch<{ description: string }>(`/projects/${projectId}/tickets/${ticketId}/improve`, {
        method: 'POST',
    });
