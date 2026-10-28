import { useInfiniteQuery } from '@tanstack/react-query';
import { api, call } from '../../api/client';
import { keys } from '../../api/queryKeys';
import type { ToolCallDto } from '../../api/types';
import { Badge, RelativeTime, RiskBadge } from '../../components/ui';
import { formatDuration } from '../../lib/time';
import { humanize, isUnreported, shortId, statusTone } from './auditFormat';
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
} from './auditParts';

export function ToolCallList({ filters, agentNames }: { filters: AuditFilters; agentNames: AgentNames }) {
  const query = useInfiniteQuery({
    queryKey: keys.auditList('tools', filters),
    queryFn: ({ pageParam }) =>
      call(() => api.GET('/api/audit/tool-calls', { params: { query: auditQuery(filters, pageParam) } })),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
  const calls = query.data?.pages.flatMap((p) => p.calls) ?? [];

  // Tool `arguments` are deliberately never rendered: the audit shows metadata only.
  return (
    <ListFrame query={query} count={calls.length} emptyTitle="No tool calls match these filters.">
      <div className="hidden overflow-x-auto rounded-lg border border-border md:block">
        <table aria-label="Tool calls" className="w-full text-left text-sm whitespace-nowrap">
          <thead className="bg-surface-2 text-xs text-muted">
            <tr>
              <Th>Time</Th>
              <Th>Agent</Th>
              <Th>Tool</Th>
              <Th>Risk</Th>
              <Th>Decision</Th>
              <Th>Outcome</Th>
              <Th>Duration</Th>
              <Th>Approval</Th>
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
                <Td>{c.toolName}</Td>
                <Td>{c.risk ? <RiskBadge risk={c.risk} /> : '—'}</Td>
                <Td>{humanize(c.decision)}</Td>
                <Td>
                  <Outcome call={c} />
                </Td>
                <Td>{formatDuration(c.durationMs)}</Td>
                <Td>{c.approvalId ? <code>{shortId(c.approvalId)}</code> : '—'}</Td>
                <Td>
                  <ThreadLink threadId={c.threadId} />
                </Td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <ul aria-label="Tool calls" className="space-y-3 md:hidden">
        {calls.map((c) => (
          <li key={c.id} className="rounded-lg border border-border bg-surface p-3 text-sm">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <span className="font-medium">{c.toolName}</span>
              <Outcome call={c} />
            </div>
            <dl className="mt-2 grid grid-cols-2 gap-x-3 gap-y-1">
              <Field label="Time">
                <RelativeTime iso={c.createdAt} />
              </Field>
              <Field label="Agent">{agentName(agentNames, c.agentId)}</Field>
              <Field label="Risk">{c.risk ? <RiskBadge risk={c.risk} /> : '—'}</Field>
              <Field label="Decision">{humanize(c.decision)}</Field>
              <Field label="Duration">{formatDuration(c.durationMs)}</Field>
              <Field label="Approval">{c.approvalId ? <code>{shortId(c.approvalId)}</code> : '—'}</Field>
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

function Outcome({ call: c }: { call: ToolCallDto }) {
  return (
    <span className="inline-flex flex-wrap items-center gap-1">
      <Badge tone={statusTone(c.status)}>{humanize(c.status)}</Badge>
      {isUnreported(c) && (
        <Badge tone="warn" title="The agent has not reported a result for this call after 5 minutes.">
          unreported
        </Badge>
      )}
    </span>
  );
}
