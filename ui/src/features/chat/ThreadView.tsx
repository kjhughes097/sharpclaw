import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { api, call } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { keys } from '../../api/queryKeys';
import type { TraceToolCallDto } from '../../api/types';
import { useLiveTurns } from '../../app/services';
import { Button, ErrorState, LoadingState, Spinner } from '../../components/ui';
import { getExpandToolDetails, setExpandToolDetails } from '../../lib/prefs';
import { Composer } from './Composer';
import { ThreadBanners } from './ThreadBanners';
import { ThreadHeader } from './ThreadHeader';
import { TurnView } from './TurnView';
import { groupTurns } from './turns';
import { useThreadMessages } from './useThreadMessages';
import { useThreadScroll } from './useThreadScroll';
import { useReplyAnnouncement, useTurnFailureToasts } from './useThreadSignals';
import { useTurnApprovals } from './useTurnApprovals';

/** One chat: header, banners, the message list with live turns, and the composer. */
export function ThreadView({
  threadId,
  onOpenTrace,
  highlightMessageId,
  backLink,
}: {
  threadId: string;
  onOpenTrace: () => void;
  highlightMessageId?: string | null;
  backLink?: boolean;
}) {
  const queryClient = useQueryClient();
  const detail = useQuery({
    queryKey: keys.thread(threadId),
    queryFn: () => call(() => api.GET('/api/threads/{id}', { params: { path: { id: threadId } } })),
  });
  const { latest, rows, hasMoreBefore, loadEarlier, loadingEarlier, earlierError, addOptimistic } =
    useThreadMessages(threadId);
  const liveTurns = useLiveTurns(threadId);
  const turns = useMemo(() => groupTurns(rows), [rows]);
  const hasTools = turns.some((t) => t.tools.length > 0);
  const trace = useQuery({
    queryKey: keys.trace(threadId),
    queryFn: () => call(() => api.GET('/api/threads/{id}/trace', { params: { path: { id: threadId } } })),
    enabled: hasTools,
  });
  const traceByCall = useMemo(() => {
    const map = new Map<string, TraceToolCallDto>();
    for (const turn of trace.data?.turns ?? [])
      for (const call of turn.toolCalls) map.set(call.toolCallId, call);
    return map;
  }, [trace.data]);
  const approvals = useTurnApprovals(threadId, detail.data?.pendingApprovals);
  const [expandTools, setExpandTools] = useState(getExpandToolDetails);

  useTurnFailureToasts(threadId);
  const announcement = useReplyAnnouncement(liveTurns);

  // Pinned to the bottom unless the user scrolled up.
  const liveLength = liveTurns.reduce((n, t) => n + t.text.length + t.tools.length, 0);
  const scroll = useThreadScroll(threadId, `${rows.length}:${turns.length}:${liveLength}`);
  const pinToBottom = scroll.pin;

  // ----- Sending -----
  const send = useMutation({
    mutationFn: (content: string) =>
      call(() =>
        api.POST('/api/threads/{id}/messages', {
          params: { path: { id: threadId } },
          body: { content, unattended: false },
        }),
      ),
  });
  const [sendError, setSendError] = useState<string | null>(null);
  const onSend = useCallback(
    async (content: string) => {
      setSendError(null);
      try {
        const { message } = await send.mutateAsync(content);
        addOptimistic(message, content);
        pinToBottom();
        void queryClient.invalidateQueries({ queryKey: keys.thread(threadId) });
        return true;
      } catch (error) {
        setSendError(errorMessage(error));
        return false;
      }
    },
    [send, addOptimistic, queryClient, threadId, pinToBottom],
  );

  const onLoadEarlier = () => {
    scroll.beforePrepend();
    void loadEarlier();
  };

  // Scroll a trace-selected turn into view.
  useEffect(() => {
    if (!highlightMessageId) return;
    document.getElementById(`msg-${highlightMessageId}`)?.scrollIntoView({ block: 'center' });
  }, [highlightMessageId, rows.length]);

  if (detail.isPending) return <LoadingState label="Loading chat" />;
  if (detail.error)
    return <ErrorState message={errorMessage(detail.error)} onRetry={() => void detail.refetch()} />;

  const { thread, agent, inFlight, queued } = detail.data;
  const liveByCorrelation = new Map(liveTurns.map((t) => [t.correlationId, t]));
  // Live turns not yet backed by a user row (e.g. the user row is on an unloaded page) still show.
  const turnApprovals = (correlationId: string) =>
    [...approvals.values()].filter((a) => a.correlationId === correlationId);

  return (
    <section aria-label={thread.title ?? 'Chat'} className="relative flex min-h-0 flex-1 flex-col">
      <ThreadHeader
        threadId={threadId}
        thread={thread}
        agent={agent}
        backLink={backLink}
        onOpenTrace={onOpenTrace}
        expandTools={expandTools}
        onExpandToolsChange={(checked) => {
          setExpandTools(checked);
          setExpandToolDetails(checked);
        }}
      />

      <ThreadBanners agent={agent} queued={queued} />

      <div
        ref={scroll.scrollRef}
        onScroll={scroll.onScroll}
        className="min-h-0 flex-1 overflow-y-auto px-3 py-3"
        data-testid="message-scroll"
      >
        {(thread.context?.droppedTurns ?? 0) > 0 && (
          <p role="note" className="mb-3 rounded-md bg-surface-2 p-2 text-sm text-muted">
            The model no longer sees the oldest {thread.context!.droppedTurns} turns of this conversation.
          </p>
        )}
        {hasMoreBefore && (
          <div className="mb-3 text-center">
            <Button onClick={onLoadEarlier} busy={loadingEarlier}>
              Load earlier messages
            </Button>
            {earlierError != null && <p className="mt-1 text-sm text-danger">{errorMessage(earlierError)}</p>}
          </div>
        )}
        {latest.isPending ? (
          <LoadingState label="Loading messages" />
        ) : latest.error ? (
          <ErrorState message={errorMessage(latest.error)} onRetry={() => void latest.refetch()} />
        ) : turns.length === 0 && liveTurns.length === 0 ? (
          <p className="p-6 text-center text-sm text-muted">Say hello to {agent.name}.</p>
        ) : (
          <ol className="space-y-5" aria-label="Messages">
            {turns.map((turn) => (
              <TurnView
                key={turn.correlationId}
                turn={turn}
                live={liveByCorrelation.get(turn.correlationId)}
                approvals={turnApprovals(turn.correlationId)}
                trace={traceByCall.size > 0 ? traceByCall : undefined}
                expandTools={expandTools}
                claimedAt={
                  inFlight?.correlationId === turn.correlationId ? (inFlight.claimedAt ?? null) : null
                }
                highlighted={!!highlightMessageId && turn.user?.id === highlightMessageId}
                onResend={(content) => void onSend(content)}
              />
            ))}
          </ol>
        )}
      </div>

      {scroll.showJump && (
        <button
          type="button"
          onClick={scroll.jumpToLatest}
          className="absolute bottom-24 left-1/2 z-10 min-h-11 -translate-x-1/2 rounded-full bg-accent px-4 text-sm text-accent-fg shadow-lg"
        >
          Jump to latest ↓
        </button>
      )}

      <div className="sr-only" role="status" aria-live="polite">
        {announcement}
      </div>

      {sendError && (
        <p role="alert" className="mx-3 mb-1 rounded-md bg-danger-soft p-2 text-sm">
          {sendError}
        </p>
      )}
      {send.isPending && (
        <p className="sr-only" role="status">
          <Spinner small /> Sending
        </p>
      )}
      <Composer
        threadId={threadId}
        onSend={onSend}
        busy={send.isPending}
        turnInFlight={!!inFlight || queued > 0}
      />
    </section>
  );
}
