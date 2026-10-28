import { useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { NavLink, useNavigate } from 'react-router';
import { api, call } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { keys } from '../../api/queryKeys';
import type { AgentDto } from '../../api/types';
import { Badge, Button, ErrorState, LoadingState, RelativeTime, Spinner } from '../../components/ui';

/**
 * The SESSIONS sidebar (3.3.1 spec section 6.2): "+ New session" for the selected agent (and
 * model), then that agent's threads newest first, with the in-flight, approval and queued badges.
 * "Session" is UI wording for a thread.
 */
export function SessionList({
  agent,
  model,
  activeId,
  onCollapse,
  onNavigate,
}: {
  agent: AgentDto | undefined;
  /** The model picked for the next new session; "" means the agent's default. */
  model: string;
  activeId: string | undefined;
  onCollapse?: () => void;
  onNavigate?: () => void;
}) {
  const [archived, setArchived] = useState(false);
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const agentId = agent?.id;
  const threads = useInfiniteQuery({
    queryKey: keys.threadList({ agentId, archived }),
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET('/api/threads', {
          params: { query: { agentId, archived, limit: 50, cursor: pageParam ?? undefined } },
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    enabled: !!agentId,
  });
  const create = useMutation({
    mutationFn: (a: AgentDto) =>
      call(() => api.POST('/api/threads', { body: { agentId: a.id, title: null, model: model || null } })),
    onSuccess: ({ thread }) => {
      void queryClient.invalidateQueries({ queryKey: keys.threads });
      onNavigate?.();
      navigate(`/prompts/${thread.id}`);
    },
  });
  const rows = threads.data?.pages.flatMap((p) => p.threads) ?? [];

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className="flex items-center justify-between px-2 pt-2">
        <h2 className="text-xs tracking-wider text-accent uppercase">Sessions</h2>
        {onCollapse && (
          <button
            type="button"
            onClick={onCollapse}
            aria-label="Hide sessions"
            className="inline-flex min-h-8 min-w-8 items-center justify-center rounded border border-border text-xs text-muted hover:bg-surface-2"
          >
            ⇤
          </button>
        )}
      </div>
      <div className="space-y-1 p-2">
        <Button
          variant="primary"
          className="w-full justify-start"
          disabled={!agent?.enabled}
          busy={create.isPending}
          onClick={() => agent && create.mutate(agent)}
        >
          + New session
        </Button>
        {create.error && (
          <p role="alert" className="text-xs text-danger">
            {errorMessage(create.error)}
          </p>
        )}
        <label className="inline-flex min-h-8 items-center gap-2 px-1 text-xs text-muted">
          <input type="checkbox" checked={archived} onChange={(e) => setArchived(e.target.checked)} />
          Archived
        </label>
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto px-2 pb-2">
        {!agentId ? (
          <p className="p-2 text-sm text-muted">Choose an agent.</p>
        ) : threads.isPending ? (
          <LoadingState label="Loading sessions" />
        ) : threads.error ? (
          <ErrorState message={errorMessage(threads.error)} onRetry={() => void threads.refetch()} />
        ) : rows.length === 0 ? (
          <p className="p-2 text-sm text-muted">{archived ? 'No archived sessions.' : 'No sessions yet.'}</p>
        ) : (
          <ul aria-label="Sessions" className="space-y-0.5">
            {rows.map((t) => (
              <li key={t.id}>
                <NavLink
                  to={`/prompts/${t.id}`}
                  onClick={onNavigate}
                  aria-current={t.id === activeId ? 'page' : undefined}
                  className={`block rounded px-2 py-1.5 hover:bg-surface-2 ${t.id === activeId ? 'bg-accent-soft text-accent' : ''}`}
                >
                  <span className="flex items-center gap-1.5">
                    <span className="min-w-0 flex-1 truncate">{t.title || 'Untitled session'}</span>
                    {t.inFlight && (
                      <>
                        <Spinner small />
                        <span className="sr-only">replying</span>
                      </>
                    )}
                    {t.pendingApprovals > 0 && (
                      <Badge tone="danger">
                        {t.pendingApprovals} approval{t.pendingApprovals === 1 ? '' : 's'}
                      </Badge>
                    )}
                    {t.queued > 0 && <Badge tone="warn">{t.queued} queued</Badge>}
                  </span>
                  <span className="flex gap-1 truncate text-xs text-muted">
                    <RelativeTime iso={t.lastMessageAt} />
                    {t.preview && <span className="truncate">· {t.preview}</span>}
                  </span>
                </NavLink>
              </li>
            ))}
          </ul>
        )}
        {threads.hasNextPage && (
          <div className="p-2 text-center">
            <Button onClick={() => void threads.fetchNextPage()} busy={threads.isFetchingNextPage}>
              Load more
            </Button>
          </div>
        )}
      </div>
    </div>
  );
}
