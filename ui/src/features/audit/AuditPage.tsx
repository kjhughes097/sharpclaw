import { useInfiniteQuery } from '@tanstack/react-query';
import { Tabs } from 'radix-ui';
import { useId, type ReactNode } from 'react';
import { Link, useSearchParams } from 'react-router';
import { api, call } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useAgents, useSystem } from '../../api/hooks';
import { keys } from '../../api/queryKeys';
import type { LlmCallDto, ToolCallDto } from '../../api/types';
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
import {
  LLM_STATUSES,
  TIME_WINDOWS,
  TOOL_OUTCOMES,
  humanize,
  isUnreported,
  shortId,
  sinceFor,
  statusTone,
  type AuditTab,
} from './auditFormat';

const PAGE_SIZE = 100;

/** Filters as they appear in the URL; undefined means "not filtered". */
interface AuditFilters extends Record<string, string | undefined> {
  agent: string | undefined;
  status: string | undefined;
  window: string | undefined;
  threadId: string | undefined;
}

type AgentNames = ReadonlyMap<string, string>;

/**
 * `/audit` (3.3 spec section 9.8): LLM and tool calls, newest first, metadata only. Tab and filters
 * live in the URL so a view can be deep-linked and survives a reload.
 */
export function AuditPage() {
  const [params, setParams] = useSearchParams();
  // Loads the server clock offset: the time windows and the "unreported" marker use server time.
  useSystem();
  const agents = useAgents();

  const tab: AuditTab = params.get('tab') === 'tools' ? 'tools' : 'llm';
  const statuses: readonly string[] = tab === 'llm' ? LLM_STATUSES : TOOL_OUTCOMES;
  const rawStatus = params.get('status') ?? undefined;
  const rawWindow = params.get('window') ?? undefined;
  const filters: AuditFilters = {
    agent: params.get('agent') || undefined,
    // Values that don't belong to this tab are ignored rather than sent.
    status: rawStatus && statuses.includes(rawStatus) ? rawStatus : undefined,
    window: TIME_WINDOWS.some((w) => w.value === rawWindow) ? rawWindow : undefined,
    threadId: params.get('threadId') || undefined,
  };

  const agentNames: AgentNames = new Map((agents.data ?? []).map((a) => [a.id, a.name]));
  const agentOptions = (agents.data ?? []).map((a) => a.name);
  if (filters.agent && !agentOptions.includes(filters.agent)) agentOptions.push(filters.agent);

  const update = (changes: Record<string, string | undefined>) =>
    setParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        for (const [key, value] of Object.entries(changes)) {
          if (value) next.set(key, value);
          else next.delete(key);
        }
        return next;
      },
      { replace: true },
    );

  return (
    <div className="flex min-h-0 flex-1 flex-col overflow-y-auto">
      <div className="mx-auto w-full max-w-7xl p-4">
        <h1 className="text-xl font-semibold">Audit</h1>
        <p className="mt-1 text-sm text-muted">
          Metadata for every LLM and tool call. Prompts, replies and tool arguments are not shown.
        </p>

        <Tabs.Root
          value={tab}
          // Status values differ per tab, so switching tabs drops the status filter.
          onValueChange={(value) => update({ tab: value === 'tools' ? 'tools' : 'llm', status: undefined })}
          className="mt-4"
        >
          <Tabs.List aria-label="Audit tables" className="flex gap-1 border-b border-border">
            <TabTrigger value="llm">LLM calls</TabTrigger>
            <TabTrigger value="tools">Tool calls</TabTrigger>
          </Tabs.List>

          <Filters statuses={statuses} filters={filters} agentOptions={agentOptions} onChange={update} />

          <Tabs.Content value="llm" className="mt-4 outline-none">
            <LlmCallList filters={filters} agentNames={agentNames} />
          </Tabs.Content>
          <Tabs.Content value="tools" className="mt-4 outline-none">
            <ToolCallList filters={filters} agentNames={agentNames} />
          </Tabs.Content>
        </Tabs.Root>
      </div>
    </div>
  );
}

function TabTrigger({ value, children }: { value: AuditTab; children: ReactNode }) {
  return (
    <Tabs.Trigger
      value={value}
      className="-mb-px min-h-11 border-b-2 border-transparent px-4 text-sm font-medium text-muted data-[state=active]:border-accent data-[state=active]:text-fg"
    >
      {children}
    </Tabs.Trigger>
  );
}

function Filters({
  statuses,
  filters,
  agentOptions,
  onChange,
}: {
  statuses: readonly string[];
  filters: AuditFilters;
  agentOptions: string[];
  onChange: (changes: Record<string, string | undefined>) => void;
}) {
  return (
    <div className="mt-4 flex flex-wrap items-end gap-3">
      <SelectField label="Agent" value={filters.agent ?? ''} onChange={(v) => onChange({ agent: v })}>
        <option value="">All agents</option>
        {agentOptions.map((name) => (
          <option key={name} value={name}>
            {name}
          </option>
        ))}
      </SelectField>
      <SelectField label="Status" value={filters.status ?? ''} onChange={(v) => onChange({ status: v })}>
        <option value="">All statuses</option>
        {statuses.map((s) => (
          <option key={s} value={s}>
            {humanize(s)}
          </option>
        ))}
      </SelectField>
      <SelectField label="Time window" value={filters.window ?? ''} onChange={(v) => onChange({ window: v })}>
        <option value="">All time</option>
        {TIME_WINDOWS.map((w) => (
          <option key={w.value} value={w.value}>
            {w.label}
          </option>
        ))}
      </SelectField>
      {filters.threadId && (
        <button
          type="button"
          onClick={() => onChange({ threadId: undefined })}
          aria-label={`Thread ${shortId(filters.threadId)}: remove thread filter`}
          className="inline-flex min-h-11 items-center gap-2 rounded-full border border-border bg-accent-soft px-4 text-sm"
        >
          Thread {shortId(filters.threadId)}
          <span aria-hidden="true">×</span>
        </button>
      )}
    </div>
  );
}

function SelectField({
  label,
  value,
  onChange,
  children,
}: {
  label: string;
  value: string;
  onChange: (value: string | undefined) => void;
  children: ReactNode;
}) {
  const id = useId();
  return (
    <div className="flex min-w-36 flex-1 flex-col gap-1 sm:flex-none">
      <label htmlFor={id} className="text-sm font-medium">
        {label}
      </label>
      <select
        id={id}
        value={value}
        onChange={(e) => onChange(e.target.value || undefined)}
        className="min-h-11 rounded-md border border-border bg-surface px-3 text-sm text-fg"
      >
        {children}
      </select>
    </div>
  );
}

function auditQuery(filters: AuditFilters, before: string | undefined) {
  return {
    agent: filters.agent,
    status: filters.status,
    threadId: filters.threadId,
    since: sinceFor(filters.window),
    limit: PAGE_SIZE,
    before,
  };
}

function LlmCallList({ filters, agentNames }: { filters: AuditFilters; agentNames: AgentNames }) {
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

function ToolCallList({ filters, agentNames }: { filters: AuditFilters; agentNames: AgentNames }) {
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

/** Loading, error, empty and "Load more" around a list. */
function ListFrame({
  query,
  count,
  emptyTitle,
  children,
}: {
  query: {
    isPending: boolean;
    isError: boolean;
    error: unknown;
    refetch: () => unknown;
    hasNextPage: boolean;
    isFetchingNextPage: boolean;
    isFetchNextPageError: boolean;
    fetchNextPage: () => unknown;
  };
  count: number;
  emptyTitle: string;
  children: ReactNode;
}) {
  if (query.isPending) return <LoadingState label="Loading calls" />;
  if (query.isError && !query.isFetchNextPageError)
    return <ErrorState message={errorMessage(query.error)} onRetry={() => void query.refetch()} />;
  if (count === 0) return <EmptyState title={emptyTitle} />;
  return (
    <>
      {children}
      {query.isFetchNextPageError && (
        <ErrorState message={errorMessage(query.error)} onRetry={() => void query.fetchNextPage()} />
      )}
      {query.hasNextPage && !query.isFetchNextPageError && (
        <div className="mt-4 flex justify-center">
          <Button busy={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>
            Load more
          </Button>
        </div>
      )}
    </>
  );
}

function Th({ children }: { children: ReactNode }) {
  return (
    <th scope="col" className="px-3 py-2 font-medium">
      {children}
    </th>
  );
}

function Td({ children }: { children: ReactNode }) {
  return <td className="px-3 py-2 align-middle">{children}</td>;
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="min-w-0">
      <dt className="text-xs text-muted">{label}</dt>
      <dd className="break-words">{children}</dd>
    </div>
  );
}

function ThreadLink({ threadId }: { threadId: string }) {
  return (
    <Link to={`/chats/${threadId}`} className="inline-flex min-h-11 items-center text-accent underline">
      <span className="sr-only">Thread </span>
      <code>{shortId(threadId)}</code>
    </Link>
  );
}

function agentName(names: AgentNames, agentId: string): string {
  return names.get(agentId) ?? shortId(agentId);
}

function tokensInOut(c: LlmCallDto): string {
  if (!c.usageKnown) return 'unknown';
  return `${formatTokens(c.promptTokens)} / ${formatTokens(c.completionTokens)}`;
}

function estimated(c: LlmCallDto): string {
  return c.estimatedPromptTokens === null || c.estimatedPromptTokens === undefined
    ? '—'
    : formatTokens(c.estimatedPromptTokens);
}
