import { useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { NavLink, useNavigate } from 'react-router';
import { api, call } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useAgents } from '../../api/hooks';
import { keys } from '../../api/queryKeys';
import {
  Badge,
  Button,
  Dialog,
  EmptyState,
  ErrorState,
  LoadingState,
  RelativeTime,
  Spinner,
} from '../../components/ui';
import { formatUsd } from '../../lib/time';

function NewChatDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const agents = useAgents();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const create = useMutation({
    mutationFn: (agentId: string) => call(() => api.POST('/api/threads', { body: { agentId, title: null } })),
    onSuccess: ({ thread }) => {
      void queryClient.invalidateQueries({ queryKey: keys.threads });
      onOpenChange(false);
      navigate(`/chats/${thread.id}`);
    },
  });
  const enabled = agents.data?.filter((a) => a.enabled) ?? [];

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title="New chat"
      description="Choose an agent to talk to."
    >
      {agents.isPending ? (
        <Spinner label="Loading agents" />
      ) : agents.error ? (
        <ErrorState message={errorMessage(agents.error)} onRetry={() => void agents.refetch()} />
      ) : enabled.length === 0 ? (
        <p className="text-sm text-muted">No enabled agents. Create or enable one on the Agents page.</p>
      ) : (
        <ul className="space-y-2">
          {enabled.map((agent) => (
            <li key={agent.id}>
              <button
                type="button"
                disabled={create.isPending}
                onClick={() => create.mutate(agent.id)}
                className="flex min-h-11 w-full items-center justify-between rounded-md border border-border px-3 py-2 text-left hover:bg-surface-2 disabled:opacity-50"
              >
                <span>
                  <span className="font-medium">{agent.name}</span>
                  <span className="block text-xs text-muted">{agent.model}</span>
                </span>
                <span className="text-xs text-muted">{formatUsd(agent.remainingUsd)} left today</span>
              </button>
            </li>
          ))}
        </ul>
      )}
      {create.error && (
        <p role="alert" className="mt-2 text-sm text-danger">
          {errorMessage(create.error)}
        </p>
      )}
    </Dialog>
  );
}

/** Newest activity first, "Load more" by cursor, agent filter and archived toggle (3.3 spec 9.2). */
export function ThreadList({ activeId }: { activeId?: string | undefined }) {
  const [agentId, setAgentId] = useState<string>('');
  const [archived, setArchived] = useState(false);
  const [newOpen, setNewOpen] = useState(false);
  const agents = useAgents();
  const threads = useInfiniteQuery({
    queryKey: keys.threadList({ agentId: agentId || undefined, archived }),
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET('/api/threads', {
          params: {
            query: { agentId: agentId || undefined, archived, limit: 50, cursor: pageParam ?? undefined },
          },
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
  const rows = threads.data?.pages.flatMap((p) => p.threads) ?? [];

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className="space-y-2 border-b border-border p-3">
        <div className="flex items-center justify-between">
          <h1 className="text-lg font-semibold">Chats</h1>
          <Button variant="primary" onClick={() => setNewOpen(true)}>
            New chat
          </Button>
        </div>
        <div className="flex flex-wrap items-center gap-2 text-sm">
          <label className="sr-only" htmlFor="thread-agent-filter">
            Filter by agent
          </label>
          <select
            id="thread-agent-filter"
            value={agentId}
            onChange={(e) => setAgentId(e.target.value)}
            className="min-h-11 rounded-md border border-border bg-surface px-2"
          >
            <option value="">All agents</option>
            {agents.data?.map((a) => (
              <option key={a.id} value={a.id}>
                {a.name}
              </option>
            ))}
          </select>
          <label className="inline-flex min-h-11 items-center gap-2">
            <input
              type="checkbox"
              checked={archived}
              onChange={(e) => setArchived(e.target.checked)}
              className="h-5 w-5"
            />
            Archived
          </label>
        </div>
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto">
        {threads.isPending ? (
          <LoadingState label="Loading chats" />
        ) : threads.error ? (
          <ErrorState message={errorMessage(threads.error)} onRetry={() => void threads.refetch()} />
        ) : rows.length === 0 ? (
          <EmptyState title={archived ? 'No archived chats.' : 'No chats yet.'}>
            {archived ? 'Archived chats appear here.' : 'Start one with “New chat”.'}
          </EmptyState>
        ) : (
          <ul aria-label="Chats">
            {rows.map((t) => (
              <li key={t.id}>
                <NavLink
                  to={`/chats/${t.id}`}
                  aria-current={t.id === activeId ? 'page' : undefined}
                  className={`block border-b border-border px-3 py-2 hover:bg-surface-2 ${t.id === activeId ? 'bg-accent-soft' : ''}`}
                >
                  <div className="flex items-center gap-2">
                    <span className="min-w-0 flex-1 truncate font-medium">{t.title || 'Untitled chat'}</span>
                    <span className="shrink-0 text-xs text-muted">
                      <RelativeTime iso={t.lastMessageAt} />
                    </span>
                  </div>
                  <div className="mt-0.5 flex items-center gap-2">
                    <Badge>{t.agentName}</Badge>
                    {t.inFlight && (
                      <span className="inline-flex items-center gap-1 text-xs text-muted">
                        <Spinner small /> replying
                      </span>
                    )}
                    {t.pendingApprovals > 0 && (
                      <Badge tone="danger">
                        {t.pendingApprovals} approval{t.pendingApprovals === 1 ? '' : 's'}
                      </Badge>
                    )}
                    {t.queued > 0 && <Badge tone="warn">{t.queued} queued</Badge>}
                  </div>
                  {t.preview && <p className="mt-0.5 truncate text-sm text-muted">{t.preview}</p>}
                </NavLink>
              </li>
            ))}
          </ul>
        )}
        {threads.hasNextPage && (
          <div className="p-3 text-center">
            <Button onClick={() => void threads.fetchNextPage()} busy={threads.isFetchingNextPage}>
              Load more
            </Button>
          </div>
        )}
      </div>
      <NewChatDialog open={newOpen} onOpenChange={setNewOpen} />
    </div>
  );
}
