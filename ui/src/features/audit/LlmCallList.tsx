import { useInfiniteQuery } from '@tanstack/react-query';
import { api, call } from '../../api/client';
import { keys } from '../../api/queryKeys';
import { Badge, RelativeTime } from '../../components/ui';
import { formatCost, formatDuration } from '../../lib/time';
import { statusTone } from './auditFormat';
import {
  type AgentNames,
  type AuditFilters,
  Field,
  ListFrame,
  Td,
  Th,
  ThreadLink,
  agentName,
  auditQuery,
  estimated,
  tokensInOut,
} from './auditParts';

export function LlmCallList({ filters, agentNames }: { filters: AuditFilters; agentNames: AgentNames }) {
  const query = useInfiniteQuery({
    queryKey: keys.auditList('llm', filters),
    queryFn: ({ pageParam }) =>
      call(() => api.GET('/api/audit/llm-calls', { params: { query: auditQuery(filters, pageParam) } })),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
  const calls = query.data?.pages.flatMap((p) => p.calls) ?? [];
  const totalCost = calls.reduce((sum, c) => sum + c.costUsd, 0);

  return (
    <ListFrame query={query} count={calls.length} emptyTitle="No LLM calls match these filters.">
      <p className="mb-3 text-sm" aria-live="polite">
        Total cost of loaded rows: <span className="font-semibold">{formatCost(totalCost)}</span>
      </p>
      <div className="hidden overflow-x-auto rounded-lg border border-border md:block">
        <table aria-label="LLM calls" className="w-full text-left text-sm whitespace-nowrap">
          <thead className="bg-surface-2 text-xs text-muted">
            <tr>
              <Th>Time</Th>
              <Th>Agent</Th>
              <Th>Model</Th>
              <Th>Status</Th>
              <Th>Tokens in / out</Th>
              <Th>Est. prompt tokens</Th>
              <Th>Cost</Th>
              <Th>Latency</Th>
              <Th>Time to first token</Th>
              <Th>Error</Th>
              <Th>Thread</Th>
            </tr>
          </thead>
          <tbody>
            {calls.map((c) => (
              <tr key={c.id} className="border-t border-border">
                <Td>
                  <RelativeTime iso={c.createdAt} />
                </Td>
                <Td>{agentName(agentNames, c.agentId)}</Td>
                <Td>{c.model}</Td>
                <Td>
                  <Badge tone={statusTone(c.status)}>{c.status}</Badge>
                </Td>
                <Td>{tokensInOut(c)}</Td>
                <Td>{estimated(c)}</Td>
                <Td>{formatCost(c.costUsd)}</Td>
                <Td>{formatDuration(c.latencyMs)}</Td>
                <Td>{formatDuration(c.ttftMs)}</Td>
                <Td>{c.errorCode ?? '—'}</Td>
                <Td>
                  <ThreadLink threadId={c.threadId} />
                </Td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <ul aria-label="LLM calls" className="space-y-3 md:hidden">
        {calls.map((c) => (
          <li key={c.id} className="rounded-lg border border-border bg-surface p-3 text-sm">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <span className="font-medium">{c.model}</span>
              <Badge tone={statusTone(c.status)}>{c.status}</Badge>
            </div>
            <dl className="mt-2 grid grid-cols-2 gap-x-3 gap-y-1">
              <Field label="Time">
                <RelativeTime iso={c.createdAt} />
              </Field>
              <Field label="Agent">{agentName(agentNames, c.agentId)}</Field>
              <Field label="Tokens in / out">{tokensInOut(c)}</Field>
              <Field label="Est. prompt tokens">{estimated(c)}</Field>
              <Field label="Cost">{formatCost(c.costUsd)}</Field>
              <Field label="Latency">{formatDuration(c.latencyMs)}</Field>
              <Field label="Time to first token">{formatDuration(c.ttftMs)}</Field>
              <Field label="Error">{c.errorCode ?? '—'}</Field>
              <Field label="Thread">
                <ThreadLink threadId={c.threadId} />
              </Field>
            </dl>
          </li>
        ))}
      </ul>
    </ListFrame>
  );
}
