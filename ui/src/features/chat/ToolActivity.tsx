import { useState } from 'react';
import type { LiveTool } from '../../api/live';
import type { TraceToolCallDto } from '../../api/types';
import { Badge, RiskBadge } from '../../components/ui';
import { formatDuration } from '../../lib/time';
import { pretty, summariseArguments, type ToolItem } from './turns';

type Status = 'ok' | 'failed' | 'denied' | 'running';

const DENIED = new Set(['denied', 'blocked', 'expired']);

function statusOf(item: ToolItem, trace: TraceToolCallDto | undefined): Status {
  if (trace && (DENIED.has(trace.outcome) || (trace.decision && DENIED.has(trace.decision)))) return 'denied';
  if (!item.result) return 'running';
  return item.result.toolOk === false ? 'failed' : 'ok';
}

const STATUS_LABEL: Record<Status, { icon: string; text: string }> = {
  ok: { icon: '✓', text: 'ok' },
  failed: { icon: '✗', text: 'failed' },
  denied: { icon: '⊘', text: 'denied' },
  running: { icon: '…', text: 'no result' },
};

function ToolCard({
  item,
  trace,
  defaultOpen,
}: {
  item: ToolItem;
  trace?: TraceToolCallDto;
  defaultOpen: boolean;
}) {
  const [open, setOpen] = useState(defaultOpen);
  const status = statusOf(item, trace);
  const label = STATUS_LABEL[status];
  return (
    <li className="rounded-md border border-border bg-surface">
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
        className="flex min-h-11 w-full flex-wrap items-center gap-2 px-3 py-2 text-left text-sm"
      >
        <span
          aria-hidden="true"
          className={status === 'ok' ? 'text-ok' : status === 'running' ? 'text-muted' : 'text-danger'}
        >
          {label.icon}
        </span>
        <span className="font-mono font-medium">{item.name}</span>
        <span className="sr-only">{label.text}</span>
        {status !== 'ok' && status !== 'running' && <Badge tone="danger">{label.text}</Badge>}
        <span className="min-w-0 flex-1 truncate font-mono text-xs text-muted">
          {summariseArguments(item.arguments)}
        </span>
      </button>
      <div className="flex flex-wrap gap-1 px-3 pb-2">
        {item.result?.untrusted && (
          <Badge tone="warn" title="Not replayed to the model on later turns">
            untrusted · not replayed to the model on later turns
          </Badge>
        )}
        {item.result?.truncated && <Badge>truncated</Badge>}
        {trace && (
          <>
            <RiskBadge risk={trace.risk} />
            {trace.decision && <Badge>decision: {trace.decision}</Badge>}
            {trace.durationMs !== null && trace.durationMs !== undefined && (
              <Badge>{formatDuration(trace.durationMs)}</Badge>
            )}
            {trace.approvalId && <Badge tone="accent">approval required</Badge>}
          </>
        )}
      </div>
      {open && (
        <div className="space-y-2 border-t border-border p-3">
          <div>
            <p className="text-xs font-medium text-muted">Arguments</p>
            {/* Tool data is always plain text: never markdown or HTML. */}
            <pre className="max-h-64 overflow-auto rounded bg-surface-2 p-2 font-mono text-xs whitespace-pre-wrap">
              {pretty(item.arguments)}
            </pre>
          </div>
          <div>
            <p className="text-xs font-medium text-muted">Result</p>
            <pre className="max-h-64 overflow-auto rounded bg-surface-2 p-2 font-mono text-xs whitespace-pre-wrap">
              {item.result ? pretty(item.result.content) : 'No result was stored.'}
            </pre>
          </div>
        </div>
      )}
    </li>
  );
}

/**
 * The tool block of a turn: collapsed to "Used N tools" by default (or expanded, per the user's
 * preference), with live "running…" rows while the turn is in flight.
 */
export function ToolActivity({
  tools,
  interim,
  live = [],
  trace,
  expandByDefault,
}: {
  tools: ToolItem[];
  interim: string[];
  live?: LiveTool[];
  /** Enrichment by provider tool-call id, only when the trace data links them. */
  trace?: Map<string, TraceToolCallDto>;
  expandByDefault: boolean;
}) {
  const [open, setOpen] = useState(expandByDefault);
  const pendingLive = live.filter((l) => !tools.some((t) => t.toolCallId === l.toolCallId));
  if (tools.length === 0 && pendingLive.length === 0) return null;

  return (
    <div className="my-2">
      {tools.length > 0 && (
        <button
          type="button"
          aria-expanded={open}
          onClick={() => setOpen((o) => !o)}
          className="min-h-11 rounded-md px-2 text-sm text-muted hover:bg-surface-2"
        >
          {open ? '▾' : '▸'} Used {tools.length} {tools.length === 1 ? 'tool' : 'tools'}
        </button>
      )}
      {open && tools.length > 0 && (
        <div className="mt-1 space-y-2">
          {interim.map((text, i) => (
            <p key={i} className="text-sm break-words whitespace-pre-wrap text-muted">
              {text}
            </p>
          ))}
          <ul className="space-y-2">
            {tools.map((item) => (
              <ToolCard
                key={item.toolCallId}
                item={item}
                trace={trace?.get(item.toolCallId)}
                defaultOpen={false}
              />
            ))}
          </ul>
        </div>
      )}
      {pendingLive.length > 0 && (
        <ul className="mt-1 space-y-1" aria-label="Tools running">
          {pendingLive.map((tool) => (
            <li key={tool.toolCallId} className="flex items-center gap-2 text-sm text-muted">
              <span aria-hidden="true">⚙</span>
              <span className="font-mono">{tool.toolName}</span> running…
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
