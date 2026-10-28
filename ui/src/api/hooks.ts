import { useQuery } from '@tanstack/react-query';
import { api, call } from './client';
import { keys } from './queryKeys';
import { setServerTime } from '../lib/time';

/** `GET /api/system`; also records the server clock offset for countdowns. */
export function useSystem() {
  return useQuery({
    queryKey: keys.system,
    queryFn: async () => {
      const info = await call(() => api.GET('/api/system'));
      setServerTime(info.utcNow);
      return info;
    },
    staleTime: 5 * 60_000,
  });
}

export function useAgents() {
  return useQuery({
    queryKey: keys.agents,
    queryFn: () => call(() => api.GET('/api/agents')),
    select: (data) => data.agents,
  });
}

/** Pending approvals feed the nav badge and tab title; events keep them fresh. */
export function usePendingApprovals() {
  return useQuery({
    queryKey: keys.approvalList('pending'),
    queryFn: () => call(() => api.GET('/api/approvals', { params: { query: { status: 'pending', limit: 200 } } })),
    select: (data) => data.approvals,
  });
}
