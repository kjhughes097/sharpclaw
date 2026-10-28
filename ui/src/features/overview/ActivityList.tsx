import { useInfiniteQuery } from '@tanstack/react-query';
import { Link } from 'react-router';
import { api, call } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { keys } from '../../api/queryKeys';
import type { ActivityItemDto } from '../../api/types';
import { Button, ErrorState, Spinner } from '../../components/ui';
import { formatTimeOfDay } from '../../lib/time';

/** `GET /api/activity` for one agent (or all), newest first, with "Load more" by cursor. */
export function useActivity(agent: string | null, limit = 50) {
  return useInfiniteQuery({
    queryKey: keys.activityFor(agent, limit),
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET('/api/activity', {
          params: { query: { agent: agent ?? undefined, limit, before: pageParam ?? undefined } },
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
}

export function kindOf(item: ActivityItemDto): { label: string; tone: string } {
  if (item.role === 'assistant') return { label: 'reply', tone: 'text-assistant' };
  if (item.status === 'failed') return { label: 'failed', tone: 'text-danger' };
  return { label: 'prompt', tone: 'text-accent' };
}

/**
 * The MESSAGES list on agent cards and agent tabs: time, from → to, kind and a plain-text preview
 * (never markdown). A row opens its thread in the Prompts tab.
 */
export function ActivityList({
  activity,
  heightClass = 'max-h-48',
}: {
  activity: ReturnType<typeof useActivity>;
  heightClass?: string;
}) {
  const items = activity.data?.pages.flatMap((p) => p.items) ?? [];
  if (activity.isPending) return <Spinner label="Loading messages" />;
  if (activity.isError)
    return <ErrorState message={errorMessage(activity.error)} onRetry={() => void activity.refetch()} />;
  if (items.length === 0) return <p className="py-2 text-sm text-muted">No messages yet.</p>;

  return (
    <div className={`overflow-y-auto ${heightClass}`}>
      <ol aria-label="Messages" className="font-mono text-xs">
        {items.map((item) => {
          const kind = kindOf(item);
          return (
            <li key={item.messageId}>
              <Link
                to={`/prompts/${item.threadId}`}
                className="grid grid-cols-[4.5rem_minmax(0,9rem)_3.5rem_minmax(0,1fr)] gap-2 rounded px-1 py-0.5 hover:bg-surface-2"
                title={item.threadTitle ?? undefined}
              >
                <span className="text-muted">{formatTimeOfDay(item.createdAt)}</span>
                <span className="truncate">
                  {item.from} <span className="text-muted">→</span> {item.to}
                </span>
                <span className={kind.tone}>{kind.label}</span>
                <span className="truncate">{item.preview}</span>
              </Link>
            </li>
          );
        })}
      </ol>
      {activity.hasNextPage && (
        <div className="p-2 text-center">
          <Button onClick={() => void activity.fetchNextPage()} busy={activity.isFetchingNextPage}>
            Load more
          </Button>
        </div>
      )}
    </div>
  );
}
