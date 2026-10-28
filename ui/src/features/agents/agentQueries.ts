import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, call } from '../../api/client';
import { keys } from '../../api/queryKeys';
import type { AgentDto } from '../../api/types';

export function useAgent(name: string) {
  return useQuery({
    queryKey: keys.agent(name),
    queryFn: () => call(() => api.GET('/api/agents/{idOrName}', { params: { path: { idOrName: name } } })),
  });
}

export function useAgentTokens(name: string) {
  return useQuery({
    queryKey: keys.tokens(name),
    queryFn: () =>
      call(() => api.GET('/api/agents/{idOrName}/tokens', { params: { path: { idOrName: name } } })),
    select: (data) => data.tokens,
  });
}

export function useAgentLlmCalls(name: string) {
  return useQuery({
    queryKey: keys.auditList('llm', { agent: name, limit: '20' }),
    queryFn: () =>
      call(() => api.GET('/api/audit/llm-calls', { params: { query: { agent: name, limit: 20 } } })),
    select: (data) => data.calls,
  });
}

export interface AgentChanges {
  model?: string;
  dailyBudgetUsd?: number;
  enabled?: boolean;
  contextWindowTokens?: number;
}

/** `PATCH /api/agents/{idOrName}`; fields left out are sent as null (unchanged). */
export function useUpdateAgent() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ name, changes }: { name: string; changes: AgentChanges }) =>
      call(() =>
        api.PATCH('/api/agents/{idOrName}', {
          params: { path: { idOrName: name } },
          body: {
            model: changes.model ?? null,
            dailyBudgetUsd: changes.dailyBudgetUsd ?? null,
            enabled: changes.enabled ?? null,
            contextWindowTokens: changes.contextWindowTokens,
          },
        }),
      ),
    onSuccess: (agent: AgentDto) => {
      queryClient.setQueryData(keys.agent(agent.name), agent);
      void queryClient.invalidateQueries({ queryKey: keys.agents, exact: true });
    },
  });
}

/**
 * Mutations that return a token value: `gcTime: 0` so the finished mutation (whose state holds
 * the value) leaves the mutation cache as soon as the caller resets it (3.3 spec section 9.7).
 */
export const SECRET_MUTATION = { gcTime: 0 } as const;

// ----- Form validation shared by the create and edit forms -----

export const NAME_HINT =
  'Lowercase letters, digits and hyphens; 2–32 characters, starting with a letter or digit.';
export const MODEL_HINT = 'An OpenRouter model id, e.g. openai/gpt-4o-mini';
export const CONTEXT_MIN = 1000;
export const CONTEXT_MAX = 2_000_000;

export function budgetError(value: string): string | null {
  const n = Number(value);
  if (value.trim() === '' || !Number.isFinite(n) || n < 0) return 'Enter an amount of 0 or more.';
  return null;
}

export function contextWindowError(value: string): string | null {
  if (value.trim() === '') return null;
  const n = Number(value);
  if (!Number.isInteger(n) || n < CONTEXT_MIN || n > CONTEXT_MAX)
    return 'Enter a whole number from 1,000 to 2,000,000, or leave it empty.';
  return null;
}

export const inputClass =
  'min-h-11 w-full rounded-md border border-border bg-surface px-3 aria-[invalid=true]:border-danger';
