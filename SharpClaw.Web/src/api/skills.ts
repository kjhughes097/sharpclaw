import { apiFetch } from './client';

export interface SkillSummary {
    name: string;
    description: string | null;
}

export interface SkillDetail extends SkillSummary {
    promptText: string;
    command: string | null;
    args: string[] | null;
    rawContent: string | null;
}

export const getSkills = () => apiFetch<SkillSummary[]>('/skills');
export const getSkill = (name: string) => apiFetch<SkillDetail>(`/skills/${name}`);
export const updateSkill = (name: string, content: string) =>
    apiFetch<void>(`/skills/${name}`, { method: 'PUT', body: JSON.stringify({ content }) });
export const createSkill = (name: string, content: string) =>
    apiFetch<void>('/skills', { method: 'POST', body: JSON.stringify({ name, content }) });
export const deleteSkill = (name: string) =>
    apiFetch<void>(`/skills/${name}`, { method: 'DELETE' });
