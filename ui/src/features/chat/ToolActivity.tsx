import { useState } from 'react';
import { Link } from 'react-router';
import type { LiveTool } from '../../api/live';
import type { TraceLlmCallDto, TraceToolCallDto } from '../../api/types';
import { formatCost, formatCount } from '../../lib/time';
import type { ApprovalView } from '../approvals/ApprovalCard';
import { pretty, type ToolItem } from './turns';

export type ToolStatus = 'done' | 'failed' | 'denied' | 'awaiting' | 'running' | 'no_result';

const DENIED = new Set(['denied', 'blocked', 'expired']);

export function toolStatus(
  item: ToolItem,
  trace: TraceToolCallDto | undefined,
  awaitingApproval: boolean,
  turnInFlight: boolean,
): ToolStatus {
  if (trace && (DENIED.has(trace.outcome) || (trace.decision && DENIED.has(trace.decision)))) return 'denied';
  if (item.result) return item.result.toolOk === false ? 'failed' : 'done';
  if (awaitingApproval) return 'awaiting';
  return turnInFlight ? 'running' : 'no_result';
}

function seconds(ms: number | null | undefined): string {
  return ms === null || ms === undefined ? '' : ` ${(ms / 1000).toFixed(ms < 100 ? 2 : 1)}s`;
}

const STATUS_TEXT: Record<ToolStatus, { text: string; tone: string }> = {
  done: { text: 'done', tone: 'text-ok' },
  failed: { text: 'failed', tone: 'text-danger' },
  denied: { text: 'denied', tone: 'text-danger' },
  awaiting: { text: 'awaiting approval', tone: 'text-warn' },
  running: { text: 'running…', tone: 'text-warn' },
  no_result: { text: 'no result', tone: 'text-muted' },
};

/** "812 in · 96 out · gpt-a · 1.8s · $0.0024": one model round's metrics, from the trace. */
export function roundMetrics(call: TraceLlmCallDto): string {
  return [
    `${formatCount(call.promptTokens)} in`,
    `${formatCount(call.completionTokens)} out`,
    call.model,
    `${(call.latencyMs / 1000).toFixed(1)}s`,
    formatCost(call.costUsd),
  ].join(' · ');
}

const ROW = 'grid grid-cols-[1rem_3.5rem_minmax(0,12rem)_minmax(0,1fr)_auto] items-baseline gap-x-2';

/**
 * For an `ask_agent` call (3.5 spec), the recipient and its linked thread, read from the stored
 * arguments and result; null for other tools or when either can't be parsed.
 */
export function askLink(item: ToolItem): { agent: string; threadId: string } | null {
  if (item.name !== 'ask_agent' || !item.result) return null;
  try {
    const args = JSON.parse(item.arguments) as { agent?: unknown };
    const result = JSON.parse(item.result.content) as { threadId?: unknown };
    return typeof args.agent === 'string' && typeof result.threadId === 'string'
      ? { agent: args.agent, threadId: result.threadId }
      : null;
  } catch {
    return null;
  }
}

function ToolRow({
  item,
  trace,
  round,
  approval,
  turnInFlight,
  defaultOpen,
}: {
  item: ToolItem;
  trace?: TraceToolCallDto;
  /** The round's LLM call, set on the first tool of each round. */
  round?: TraceLlmCallDto;
  approval?: ApprovalView;
  turnInFlight: boolean;
  defaultOpen: boolean;
}) {
  const [open, setOpen] = useState(defaultOpen);
  const status = toolStatus(item, trace, approval?.status === 'pending', turnInFlight);
  const label = STATUS_TEXT[status];
  const note = approval?.summary ?? (trace?.risk ? `risk: ${trace.risk}` : '');
  const ask = askLink(item);
  return (
    <li>
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
        className={`${ROW} w-full rounded px-1 py-0.5 text-left hover:bg-surface-2`}
      >
        <span aria-hidden="true" className="text-muted">
          {status === 'running' ? '⟳' : '┌'}
        </span>
        <span className="text-muted">tool</span>
        <span className="truncate">{item.name}</span>
        <span className="truncate text-muted">{note}</span>
        <span className={label.tone}>
          {label.text}
          {status === 'done' || status === 'failed' ? seconds(trace?.durationMs) : ''}
        </span>
      </button>
      {ask && (
        <p className={`${ROW} px-1 text-muted`}>
          <span />
          <span />
          <Link to={`/prompts/${ask.threadId}`} className="col-span-3 truncate text-accent underline">
            Open the conversation with {ask.agent}
          </Link>
        </p>
      )}
      {round && (
        <p className={`${ROW} px-1 text-muted`}>
          <span />
          <span />
          <span className="col-span-3 truncate">{roundMetrics(round)}</span>
        </p>
      )}
      {open && (
        <div className="my-1 ml-6 space-y-2">
          <div>
            <p className="text-muted">Arguments</p>
            {/* Tool data is always plain text: never markdown or HTML. */}
            <pre className="max-h-64 overflow-auto rounded bg-surface-2 p-2 whitespace-pre-wrap">
              {pretty(item.arguments)}
            </pre>
          </div>
          <div>
            <p className="text-muted">
              Result
              {item.result?.untrusted && ' · untrusted, not replayed to the model on later turns'}
              {item.result?.truncated && ' · truncated'}
            </p>
            <pre className="max-h-64 overflow-auto rounded bg-surface-2 p-2 whitespace-pre-wrap">
              {item.result ? pretty(item.result.content) : 'No result was stored.'}
            </pre>
          </div>
        </div>
      )}
    </li>
  );
}

/**
 * A turn's tool activity as compact rows (3.3.1 spec section 6.3): status glyph, kind, tool name,
 * the approval or risk note, and the status with its duration. A muted line under each model
 * round's first tool shows that round's tokens, model, latency and cost. Clicking a row shows its
 * arguments and result, as plain text.
 */
export function ToolActivity({
  tools,
  interim,
  live = [],
  trace,
  llmCalls,
  approvals = [],
  turnInFlight = false,
  expandByDefault,
}: {
  tools: ToolItem[];
  interim: string[];
  live?: LiveTool[];
  /** Trace enrichment by provider tool-call id. */
  trace?: Map<string, TraceToolCallDto>;
  /** The trace's LLM calls by id, for the per-round metrics line. */
  llmCalls?: Map<string, TraceLlmCallDto>;
  approvals?: ApprovalView[];
  turnInFlight?: boolean;
  expandByDefault: boolean;
}) {
  const pendingLive = live.filter((l) => !tools.some((t) => t.toolCallId === l.toolCallId));
  if (tools.length === 0 && pendingLive.length === 0) return null;

  const seenRounds = new Set<string>();
  const roundFor = (t: TraceToolCallDto | undefined) => {
    const id = t?.llmCallId;
    if (!id || seenRounds.has(id)) return undefined;
    seenRounds.add(id);
    return llmCalls?.get(id);
  };
  // Approvals carry no tool-call id; match the turn's approvals by tool name, newest first.
  const approvalFor = (name: string) =>
    [...approvals].reverse().find((a) => a.toolName === name && a.status === 'pending') ??
    approvals.find((a) => a.toolName === name);

  return (
    <div className="my-2 border-l border-border pl-2 font-mono text-xs">
      {interim.map((text, i) => (
        <p key={i} className="mb-1 font-sans text-sm break-words whitespace-pre-wrap text-muted">
          {text}
        </p>
      ))}
      <ul aria-label="Tool activity">
        {tools.map((item) => {
          const t = trace?.get(item.toolCallId);
          return (
            <ToolRow
              key={item.toolCallId}
              item={item}
              trace={t}
              round={roundFor(t)}
              approval={approvalFor(item.name)}
              turnInFlight={turnInFlight}
              defaultOpen={expandByDefault}
            />
          );
        })}
        {pendingLive.map((tool) => {
          const approval = approvalFor(tool.toolName);
          const awaiting = approval?.status === 'pending';
          return (
            <li key={tool.toolCallId} className={`${ROW} px-1 py-0.5`}>
              <span aria-hidden="true" className="text-muted">
                ⟳
              </span>
              <span className="text-muted">tool</span>
              <span className="truncate">{tool.toolName}</span>
              <span className="truncate text-muted">{approval?.summary ?? ''}</span>
              <span className="text-warn">{awaiting ? 'awaiting approval' : 'running…'}</span>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
