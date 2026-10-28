import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { DropdownMenu } from 'radix-ui';
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Link } from 'react-router';
import { api, call } from '../../api/client';
import { budgetResetLocalTime, errorMessage, failReasonMessage } from '../../api/errors';
import { keys } from '../../api/queryKeys';
import type { ThreadDetailResponse, TraceToolCallDto } from '../../api/types';
import { useLiveTurns, useNotices, useServices } from '../../app/services';
import { useToast } from '../../app/toast';
import { Button, ErrorState, LoadingState, Meter, Spinner } from '../../components/ui';
import { getExpandToolDetails, setExpandToolDetails } from '../../lib/prefs';
import { formatTokens, serverNow } from '../../lib/time';
import type { ApprovalView } from '../approvals/ApprovalCard';
import { Composer } from './Composer';
import { TurnView } from './TurnView';
import { groupTurns } from './turns';
import { useThreadMessages } from './useThreadMessages';

const AWAY_MS = 3 * 60_000;
const NEAR_BOTTOM_PX = 48;

function ContextIndicator({ context }: { context: ThreadDetailResponse['thread']['context'] }) {
  if (!context) return null;
  const label = `~${formatTokens(context.estimatedTokens)} / ${formatTokens(context.windowTokens)} tokens`;
  return (
    <div className="w-36" title="Approximate: from the last time the gateway built this thread's context">
      <p className="text-xs text-muted">
        {label}
        <span className="sr-only"> (approximate)</span>
      </p>
      <Meter
        value={context.estimatedTokens}
        max={context.windowTokens}
        label="Context window used (approximate)"
      />
    </div>
  );
}

function TitleEditor({ threadId, title }: { threadId: string; title: string | null }) {
  const queryClient = useQueryClient();
  const toast = useToast();
  const [editing, setEditing] = useState(false);
  const [value, setValue] = useState(title ?? '');
  const rename = useMutation({
    mutationFn: (newTitle: string) =>
      call(() =>
        api.PATCH('/api/threads/{id}', {
          params: { path: { id: threadId } },
          body: { title: newTitle, archived: null },
        }),
      ),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: keys.thread(threadId) });
      void queryClient.invalidateQueries({ queryKey: keys.threads });
    },
    onError: (error) => toast(errorMessage(error), 'error'),
  });

  if (!editing)
    return (
      <button
        type="button"
        className="min-h-11 min-w-0 truncate rounded px-1 text-left text-base font-semibold hover:bg-surface-2"
        onClick={() => {
          setValue(title ?? '');
          setEditing(true);
        }}
        title="Rename"
      >
        {title || 'Untitled chat'}
        <span className="sr-only"> (rename)</span>
      </button>
    );

  const save = () => {
    const trimmed = value.trim();
    setEditing(false);
    if (trimmed && trimmed !== title && trimmed.length <= 120) rename.mutate(trimmed);
  };
  return (
    <input
      aria-label="Chat title"
      // eslint-disable-next-line jsx-a11y/no-autofocus
      autoFocus
      maxLength={120}
      value={value}
      onChange={(e) => setValue(e.target.value)}
      onBlur={save}
      onKeyDown={(e) => {
        if (e.key === 'Enter') save();
        if (e.key === 'Escape') setEditing(false);
      }}
      className="min-h-11 min-w-0 flex-1 rounded-md border border-border bg-surface px-2 font-semibold"
    />
  );
}

/** Keeps decided approvals of the in-flight turn on screen (muted) after they leave the pending list. */
function useTurnApprovals(threadId: string, pending: ThreadDetailResponse['pendingApprovals'] | undefined) {
  const [known, setKnown] = useState<Map<string, ApprovalView & { correlationId: string }>>(new Map());
  useEffect(() => setKnown(new Map()), [threadId]);

  useEffect(() => {
    if (!pending) return;
    setKnown((current) => {
      const next = new Map(current);
      const ids = new Set(pending.map((p) => p.id));
      for (const p of pending) next.set(p.id, { ...p, status: 'pending', threadId });
      for (const [id, view] of next)
        if (!ids.has(id) && view.status === 'pending') next.set(id, { ...view, status: 'decided' });
      return next;
    });
  }, [pending, threadId]);

  // Fetch the outcome of approvals that left the pending list (approved, denied or expired).
  const undecided = [...known.values()].filter((v) => v.status === 'decided').map((v) => v.id);
  useEffect(() => {
    for (const id of undecided)
      call(() => api.GET('/api/approvals/{id}', { params: { path: { id } } }))
        .then(({ approval }) =>
          setKnown((current) =>
            new Map(current).set(id, {
              ...current.get(id)!,
              status: approval.status,
              decidedBy: approval.decidedBy,
              decidedByUser: approval.decidedByUser,
            }),
          ),
        )
        .catch(() =>
          setKnown((current) => new Map(current).set(id, { ...current.get(id)!, status: 'expired' })),
        );
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [undecided.join(',')]);

  return known;
}

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
  const toast = useToast();
  const { notices } = useServices();
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
        pinnedRef.current = true;
        void queryClient.invalidateQueries({ queryKey: keys.thread(threadId) });
        return true;
      } catch (error) {
        setSendError(errorMessage(error));
        return false;
      }
    },
    [send, addOptimistic, queryClient, threadId],
  );

  // ----- Turn failure notices from the stream -----
  const allNotices = useNotices();
  useEffect(() => {
    for (const notice of allNotices.filter((n) => n.threadId === threadId)) {
      toast(failReasonMessage(notice.code), 'error');
      notices.dismiss(notice.correlationId);
    }
  }, [allNotices, threadId, toast, notices]);

  // ----- Live region: announce start and end of a reply, never each delta -----
  const [announcement, setAnnouncement] = useState('');
  const liveIds = liveTurns.map((t) => t.correlationId).join(',');
  const previousLive = useRef<string>('');
  useEffect(() => {
    const before = new Set(previousLive.current.split(',').filter(Boolean));
    const now = new Set(liveIds.split(',').filter(Boolean));
    if ([...now].some((id) => !before.has(id))) setAnnouncement('Assistant is replying');
    else if ([...before].some((id) => !now.has(id))) setAnnouncement('Reply complete');
    previousLive.current = liveIds;
  }, [liveIds]);

  // ----- Scrolling: pinned to the bottom unless the user scrolled up -----
  const scrollRef = useRef<HTMLDivElement>(null);
  const pinnedRef = useRef(true);
  const [showJump, setShowJump] = useState(false);
  const prependRef = useRef<{ height: number; top: number } | null>(null);
  const liveLength = liveTurns.reduce((n, t) => n + t.text.length + t.tools.length, 0);

  const onScroll = () => {
    const el = scrollRef.current;
    if (!el) return;
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < NEAR_BOTTOM_PX;
    pinnedRef.current = atBottom;
    if (atBottom) setShowJump(false);
  };

  useLayoutEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    if (prependRef.current) {
      // Older rows were prepended: keep the same content in view.
      el.scrollTop = prependRef.current.top + (el.scrollHeight - prependRef.current.height);
      prependRef.current = null;
      return;
    }
    if (pinnedRef.current) el.scrollTop = el.scrollHeight;
    else setShowJump(true);
  }, [rows.length, liveLength, turns.length]);

  useEffect(() => {
    pinnedRef.current = true;
    setShowJump(false);
  }, [threadId]);

  const jumpToLatest = () => {
    const el = scrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
    pinnedRef.current = true;
    setShowJump(false);
  };

  const onLoadEarlier = () => {
    const el = scrollRef.current;
    if (el) prependRef.current = { height: el.scrollHeight, top: el.scrollTop };
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
  const lastSeen = agent.lastSeenAt ? Date.parse(agent.lastSeenAt) : null;
  const away = queued > 0 && (lastSeen === null || serverNow() - lastSeen > AWAY_MS);
  const budgetUsed = agent.dailyBudgetUsd > 0 ? agent.remainingUsd <= 0 : agent.dailyBudgetUsd === 0;
  const liveByCorrelation = new Map(liveTurns.map((t) => [t.correlationId, t]));
  // Live turns not yet backed by a user row (e.g. the user row is on an unloaded page) still show.
  const turnApprovals = (correlationId: string) =>
    [...approvals.values()].filter((a) => a.correlationId === correlationId);

  return (
    <section aria-label={thread.title ?? 'Chat'} className="relative flex min-h-0 flex-1 flex-col">
      <header className="flex flex-wrap items-center gap-2 border-b border-border bg-surface px-3 py-1">
        {backLink && (
          <Link
            to="/chats"
            className="inline-flex min-h-11 min-w-11 items-center justify-center rounded-md hover:bg-surface-2 md:hidden"
            aria-label="Back to chats"
          >
            ←
          </Link>
        )}
        <div className="flex min-w-0 flex-1 flex-col">
          <TitleEditor key={thread.title ?? ''} threadId={threadId} title={thread.title ?? null} />
          <p className="truncate px-1 text-xs text-muted">
            {agent.name} · {agent.model}
            {thread.archived && ' · archived'}
          </p>
        </div>
        <ContextIndicator context={thread.context} />
        <DropdownMenu.Root modal={false}>
          <DropdownMenu.Trigger
            className="inline-flex min-h-11 min-w-11 items-center justify-center rounded-md hover:bg-surface-2"
            aria-label="Chat menu"
          >
            ⋯
          </DropdownMenu.Trigger>
          <DropdownMenu.Portal>
            <DropdownMenu.Content
              align="end"
              className="z-30 min-w-56 rounded-md border border-border bg-surface p-1 shadow-lg"
            >
              <DropdownMenu.Item
                className="flex min-h-11 cursor-pointer items-center rounded px-3 text-sm outline-none data-[highlighted]:bg-surface-2"
                onSelect={onOpenTrace}
              >
                Trace
              </DropdownMenu.Item>
              <ArchiveItem threadId={threadId} archived={thread.archived ?? false} />
              <DropdownMenu.CheckboxItem
                className="flex min-h-11 cursor-pointer items-center gap-2 rounded px-3 text-sm outline-none data-[highlighted]:bg-surface-2"
                checked={expandTools}
                onCheckedChange={(checked) => {
                  setExpandTools(checked);
                  setExpandToolDetails(checked);
                }}
              >
                <span aria-hidden="true">{expandTools ? '☑' : '☐'}</span> Expand tool details by default
              </DropdownMenu.CheckboxItem>
            </DropdownMenu.Content>
          </DropdownMenu.Portal>
        </DropdownMenu.Root>
      </header>

      <div className="space-y-1 px-3 pt-2 empty:hidden">
        {!agent.enabled && (
          <p role="status" className="rounded-md bg-warn-soft p-2 text-sm">
            This agent is disabled. Messages will queue until it is enabled.
          </p>
        )}
        {budgetUsed && (
          <p role="status" className="rounded-md bg-warn-soft p-2 text-sm">
            Daily budget used up. Resets at {budgetResetLocalTime()}.
          </p>
        )}
        {away && (
          <p role="status" className="rounded-md bg-warn-soft p-2 text-sm">
            The agent hasn&apos;t connected recently.
          </p>
        )}
      </div>

      <div
        ref={scrollRef}
        onScroll={onScroll}
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

      {showJump && (
        <button
          type="button"
          onClick={jumpToLatest}
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

function ArchiveItem({ threadId, archived }: { threadId: string; archived: boolean }) {
  const queryClient = useQueryClient();
  const toast = useToast();
  const mutation = useMutation({
    mutationFn: () =>
      call(() =>
        api.PATCH('/api/threads/{id}', {
          params: { path: { id: threadId } },
          body: { title: null, archived: !archived },
        }),
      ),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: keys.thread(threadId) });
      void queryClient.invalidateQueries({ queryKey: keys.threads });
      toast(archived ? 'Chat restored.' : 'Chat archived.', 'success');
    },
    onError: (error) => toast(errorMessage(error), 'error'),
  });
  return (
    <DropdownMenu.Item
      className="flex min-h-11 cursor-pointer items-center rounded px-3 text-sm outline-none data-[highlighted]:bg-surface-2"
      onSelect={() => mutation.mutate()}
    >
      {archived ? 'Unarchive' : 'Archive'}
    </DropdownMenu.Item>
  );
}
