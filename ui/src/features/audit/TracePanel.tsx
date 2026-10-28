import { useQuery } from '@tanstack/react-query';
import { useId, useState, type ReactNode } from 'react';
import { api, call } from '../../api/client';
import { errorMessage, failReasonMessage } from '../../api/errors';
import { keys } from '../../api/queryKeys';
import type { TraceTurnDto } from '../../api/types';
import {
  Badge,
  Button,
  EmptyState,
  ErrorState,
  LoadingState,
  RelativeTime,
  RiskBadge,
} from '../../components/ui';
import { formatCost, formatDuration, formatTokens } from '../../lib/time';
import { deciderLabel } from '../approvals/decider';
import { humanize, statusTone } from './auditFormat';

/**
 * The per-turn trace of a thread (3.3 spec section 9.8): metadata only, never prompts, replies or
 * tool arguments. Renders the panel content; the chat page positions it and handles Esc. The event
 * layer invalidates `keys.trace(threadId)` on `message.completed`, so it refetches while open.
 */
export function TracePanel({
  threadId,
  onClose,
  onSelectTurn,
}: {
  threadId: string;
  onClose: () => void;
  onSelectTurn: (userMessageId: string) => void;
}) {
  const headingId = useId();
  const trace = useQuery({
    queryKey: keys.trace(threadId),
    queryFn: () => call(() => api.GET('/api/threads/{id}/trace', { params: { path: { id: threadId } } })),
  });

  let body: ReactNode;
  if (trace.isPending) body = <LoadingState label="Loading trace" />;
  else if (trace.isError)
    body = <ErrorState message={errorMessage(trace.error)} onRetry={() => void trace.refetch()} />;
  else if (trace.data.turns.length === 0) body = <EmptyState title="No turns yet." />;
  else
    body = (
      <ol className="space-y-3 p-3">
        {trace.data.turns.map((turn, index) => (
          <TurnEntry key={turn.correlationId} turn={turn} number={index + 1} onSelect={onSelectTurn} />
        ))}
      </ol>
    );

  return (
    <section aria-labelledby={headingId} className="flex h-full min-h-0 flex-col bg-surface">
      <header className="flex items-center justify-between gap-2 border-b border-border px-3 py-1">
        <h2 id={headingId} className="text-lg font-semibold">
          Trace
        </h2>
        <Button variant="ghost" aria-label="Close trace" onClick={onClose} className="w-11 px-0">
          <span aria-hidden="true" className="text-xl leading-none">
            ×
          </span>
        </Button>
      </header>
      <div className="min-h-0 flex-1 overflow-y-auto">{body}</div>
    </section>
  );
}

function TurnEntry({
  turn,
  number,
  onSelect,
}: {
  turn: TraceTurnDto;
  number: number;
  onSelect: (userMessageId: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const detailsId = useId();
  const { totals } = turn;
  const duration =
    turn.claimedAt && turn.completedAt ? Date.parse(turn.completedAt) - Date.parse(turn.claimedAt) : null;

  return (
    <li className="rounded-lg border border-border bg-bg p-3 text-sm">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="font-medium">Turn {number}</h3>
        <Badge tone={statusTone(turn.status)}>{humanize(turn.status)}</Badge>
      </div>
      <p className="mt-1 text-xs text-muted">
        <RelativeTime iso={turn.createdAt} />
      </p>
      {turn.status === 'failed' && <p className="mt-1 text-danger">{failReasonMessage(turn.failReason)}</p>}
      <dl className="mt-2 grid grid-cols-2 gap-x-3 gap-y-1">
        <Field label="Duration">{formatDuration(duration)}</Field>
        <Field label="Cost">{formatCost(totals.costUsd)}</Field>
        <Field label="Tokens in / out">
          {formatTokens(totals.promptTokens)} / {formatTokens(totals.completionTokens)}
        </Field>
        <Field label="Calls">
          {totals.llmCalls} LLM · {totals.toolCalls} tool
        </Field>
      </dl>
      <div className="mt-2 flex flex-wrap gap-2">
        <Button aria-expanded={open} aria-controls={detailsId} onClick={() => setOpen((o) => !o)}>
          {open ? 'Hide details' : 'Details'}
        </Button>
        <Button variant="ghost" onClick={() => onSelect(turn.userMessageId)}>
          Show in chat
        </Button>
      </div>
      <div id={detailsId} hidden={!open} className="mt-3 space-y-3">
        {open && <TurnDetails turn={turn} />}
      </div>
    </li>
  );
}

function TurnDetails({ turn }: { turn: TraceTurnDto }) {
  return (
    <>
      <DetailSection title="LLM calls" empty="No LLM calls.">
        {turn.llmCalls.map((c) => (
          <li key={c.id} className="rounded-md bg-surface-2 p-2">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <span className="font-medium break-all">{c.model}</span>
              <Badge tone={statusTone(c.status)}>{c.status}</Badge>
            </div>
            <dl className="mt-1 grid grid-cols-2 gap-x-3 gap-y-1">
              <Field label="Tokens in / out">
                {c.usageKnown
                  ? `${formatTokens(c.promptTokens)} / ${formatTokens(c.completionTokens)}`
                  : 'unknown'}
              </Field>
              <Field label="Cost">{formatCost(c.costUsd)}</Field>
              <Field label="Latency">{formatDuration(c.latencyMs)}</Field>
              <Field label="Time to first token">{formatDuration(c.ttftMs)}</Field>
              {c.errorCode && <Field label="Error">{c.errorCode}</Field>}
            </dl>
          </li>
        ))}
      </DetailSection>
      <DetailSection title="Tool calls" empty="No tool calls.">
        {turn.toolCalls.map((c) => (
          <li key={c.id} className="rounded-md bg-surface-2 p-2">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <span className="font-medium break-all">{c.toolName}</span>
              <RiskBadge risk={c.risk} />
            </div>
            <dl className="mt-1 grid grid-cols-2 gap-x-3 gap-y-1">
              <Field label="Decision">{humanize(c.decision)}</Field>
              <Field label="Outcome">
                <Badge tone={statusTone(c.outcome)}>{humanize(c.outcome)}</Badge>
              </Field>
              <Field label="Duration">{formatDuration(c.durationMs)}</Field>
            </dl>
          </li>
        ))}
      </DetailSection>
      <DetailSection title="Approvals" empty="No approvals.">
        {turn.approvals.map((a) => (
          <li key={a.id} className="rounded-md bg-surface-2 p-2">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <span className="font-medium break-all">{a.toolName}</span>
              <RiskBadge risk={a.risk} />
            </div>
            <dl className="mt-1 grid grid-cols-2 gap-x-3 gap-y-1">
              <Field label="Status">
                <Badge tone={statusTone(a.status)}>{humanize(a.status)}</Badge>
              </Field>
              <Field label="Decided by">{deciderLabel(a.decidedBy, a.decidedByUser) ?? '—'}</Field>
            </dl>
          </li>
        ))}
      </DetailSection>
    </>
  );
}

function DetailSection({ title, empty, children }: { title: string; empty: string; children: ReactNode[] }) {
  return (
    <div>
      <h4 className="text-xs font-semibold tracking-wide text-muted uppercase">{title}</h4>
      {children.length === 0 ? (
        <p className="mt-1 text-muted">{empty}</p>
      ) : (
        <ul className="mt-1 space-y-2">{children}</ul>
      )}
    </div>
  );
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="min-w-0">
      <dt className="text-xs text-muted">{label}</dt>
      <dd>{children}</dd>
    </div>
  );
}
