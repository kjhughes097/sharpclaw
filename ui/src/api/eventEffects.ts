import type { InfiniteData, QueryClient } from '@tanstack/react-query';
import type { StreamEvent } from './events';
import type { LiveStore } from './live';
import { keys } from './queryKeys';
import type { ThreadDetailResponse, ThreadListResponse } from './types';

export interface ThreadNotice {
  threadId: string;
  correlationId: string;
  code: string;
}

export interface EventEffectDeps {
  queryClient: QueryClient;
  live: LiveStore;
  /** Called for an `error` event so the thread view can show a notice. */
  onTurnError?: (notice: ThreadNotice) => void;
}

/**
 * What each event does to UI state (3.3 spec section 8.3). Every effect is idempotent: invalidation
 * and buffer operations can safely run twice, and duplicate events are dropped by the manager.
 */
export function applyEvent(event: StreamEvent, { queryClient, live, onTurnError }: EventEffectDeps) {
  const invalidate = (queryKey: readonly unknown[]) => void queryClient.invalidateQueries({ queryKey });

  switch (event.type) {
    case 'thread.created':
      invalidate(keys.threads);
      break;
    case 'thread.updated': {
      const { threadId, title, archived, model } = event.data;
      // Only the paged lists have `pages`; other queries under the prefix (the agent tab's recent
      // sessions) are refreshed by the invalidation below.
      queryClient.setQueriesData<InfiniteData<ThreadListResponse>>({ queryKey: keys.threads }, (data) =>
        data && 'pages' in data
          ? {
              ...data,
              pages: data.pages.map((page) => ({
                ...page,
                threads: page.threads.map((t) => (t.id === threadId ? { ...t, title, archived, model } : t)),
              })),
            }
          : data,
      );
      queryClient.setQueryData<ThreadDetailResponse>(keys.thread(threadId), (data) =>
        data ? { ...data, thread: { ...data.thread, title, archived, model } } : data,
      );
      // Archiving moves the thread between the active and archived lists.
      invalidate(keys.threads);
      break;
    }
    case 'message.created':
      invalidate(keys.messages(event.data.threadId));
      invalidate(keys.thread(event.data.threadId));
      invalidate(keys.threads);
      break;
    case 'delta':
      live.append(event.data.threadId, event.data.correlationId, event.data.content);
      break;
    case 'snapshot':
      live.replace(event.data.threadId, event.data.correlationId, event.data.content);
      break;
    case 'tool.requested':
      live.addTool(event.data.threadId, event.data.correlationId, {
        toolCallId: event.data.toolCallId,
        toolName: event.data.toolName,
      });
      break;
    case 'message.completed':
      live.clear(event.data.correlationId);
      invalidate(keys.messages(event.data.threadId));
      invalidate(keys.thread(event.data.threadId));
      invalidate(keys.trace(event.data.threadId));
      invalidate(keys.threads);
      break;
    case 'error':
      live.clear(event.data.correlationId);
      invalidate(keys.messages(event.data.threadId));
      invalidate(keys.thread(event.data.threadId));
      invalidate(keys.trace(event.data.threadId));
      invalidate(keys.threads);
      onTurnError?.({
        threadId: event.data.threadId,
        correlationId: event.data.correlationId,
        code: event.data.code,
      });
      break;
    case 'approval.requested':
    case 'approval.decided':
      invalidate(keys.approvals);
      invalidate(keys.thread(event.data.threadId));
      invalidate(keys.threads);
      break;
    case 'ready':
      // After a reconnect, refetch what may have changed while away (a safety net on top of replay).
      if (event.reconnected) {
        invalidate(keys.threads);
        invalidate(keys.allThreadDetails);
        invalidate(keys.allMessages);
        invalidate(keys.approvals);
      }
      break;
    case 'reset':
      // The replay point was pruned: anything cached may be stale.
      live.clearAll();
      void queryClient.invalidateQueries();
      break;
  }
}
