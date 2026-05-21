import { apiFetch } from './client';

export interface ToolParameter {
    name: string;
    description: string;
    type: string;
    required: boolean;
}

export interface ToolSummary {
    name: string;
    description: string;
    parameters: ToolParameter[];
}

export const getTools = () => apiFetch<ToolSummary[]>('/tools');
export const getTool = (name: string) => apiFetch<ToolSummary>(`/tools/${name}`);
