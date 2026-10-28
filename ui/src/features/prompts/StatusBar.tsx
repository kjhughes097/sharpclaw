import type { AgentDto, ThreadDto, TraceResponse } from '../../api/types';
import { formatCost, formatCount } from '../../lib/time';

/** The thread's token and cost totals: the sum of its trace turns. */
export function traceTotals(trace: TraceResponse | undefined) {
  if (!trace) return null;
  return trace.turns.reduce(
    (sum, t) => ({
      prompt: sum.prompt + t.totals.promptTokens,
      completion: sum.completion + t.totals.completionTokens,
      cost: sum.cost + t.totals.costUsd,
    }),
    { prompt: 0, completion: 0, cost: 0 },
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <span className="whitespace-nowrap">
      <span className="text-muted">{label}</span> {children}
    </span>
  );
}

/**
 * The one-line monospace status bar (3.3.1 spec section 6.4): session, agent, the effective model,
 * context use, and the thread's tokens and cost. Wraps on phones.
 */
export function StatusBar({
  thread,
  agent,
  trace,
}: {
  thread: ThreadDto | undefined;
  agent: AgentDto | undefined;
  trace: TraceResponse | undefined;
}) {
  const totals = traceTotals(trace);
  const context = thread?.context;
  const pct =
    context && context.windowTokens > 0 ? (context.estimatedTokens / context.windowTokens) * 100 : null;

  return (
    <footer
      aria-label="Session status"
      className="flex flex-wrap gap-x-5 gap-y-0.5 border-t border-border bg-surface px-3 py-1 font-mono text-xs"
    >
      {thread ? (
        <>
          <Field label="Session:">{thread.title || 'Untitled session'}</Field>
          <Field label="Agent:">{agent?.name ?? '—'}</Field>
          <Field label="Model:">{thread.model ?? agent?.model ?? '—'}</Field>
          <Field label="Context">
            {context
              ? `${formatCount(context.estimatedTokens)} / ${formatCount(context.windowTokens)} (${pct!.toFixed(1)}%)`
              : '—'}
          </Field>
          <Field label="Tokens">
            {totals
              ? `${formatCount(totals.prompt + totals.completion)} (in ${formatCount(totals.prompt)} · out ${formatCount(totals.completion)})`
              : '—'}
          </Field>
          <Field label="Cost">{totals ? formatCost(totals.cost) : '—'}</Field>
        </>
      ) : (
        <Field label="Agent:">{agent ? `${agent.name} · ${agent.model}` : '—'}</Field>
      )}
    </footer>
  );
}
