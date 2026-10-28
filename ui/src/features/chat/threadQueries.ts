import { useQuery } from '@tanstack/react-query';
import { api, call } from '../../api/client';
import { keys } from '../../api/queryKeys';

/** `GET /api/threads/{id}`: the thread, its agent, the in-flight turn and pending approvals. */
export function useThreadDetail(threadId: string | undefined) {
  return useQuery({
    queryKey: keys.thread(threadId ?? ''),
    queryFn: () => call(() => api.GET('/api/threads/{id}', { params: { path: { id: threadId! } } })),
    enabled: !!threadId,
  });
}

/** `GET /api/threads/{id}/trace`: per-turn LLM calls, tool calls and totals (refreshed on turn end). */
export function useThreadTrace(threadId: string | undefined) {
  return useQuery({
    queryKey: keys.trace(threadId ?? ''),
    queryFn: () => call(() => api.GET('/api/threads/{id}/trace', { params: { path: { id: threadId! } } })),
    enabled: !!threadId,
  });
}
