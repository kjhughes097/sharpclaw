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
    createdAt: string;
    updatedAt: string;
}

export const getProjects = () => apiFetch<ProjectSummary[]>('/projects');

export const getProjectTickets = (projectId: string) =>
    apiFetch<TicketSummary[]>(`/projects/${projectId}/tickets`);

export const createTicket = (projectId: string, data: { title: string; description?: string }) =>
    apiFetch<TicketSummary>(`/projects/${projectId}/tickets`, {
        method: 'POST',
        body: JSON.stringify(data),
    });

export const updateTicket = (projectId: string, ticketId: string, data: { title?: string; description?: string; status?: string }) =>
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
