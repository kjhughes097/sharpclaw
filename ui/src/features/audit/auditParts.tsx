import { type ReactNode } from 'react';
import { Link } from 'react-router';
import { errorMessage } from '../../api/errors';
import type { LlmCallDto } from '../../api/types';
import { Button, EmptyState, ErrorState, LoadingState } from '../../components/ui';
import { formatTokens } from '../../lib/time';
import { shortId, sinceFor } from './auditFormat';

const PAGE_SIZE = 100;

/** Filters as they appear in the URL; undefined means "not filtered". */
export interface AuditFilters extends Record<string, string | undefined> {
  agent: string | undefined;
  status: string | undefined;
  window: string | undefined;
  threadId: string | undefined;
}

export type AgentNames = ReadonlyMap<string, string>;

export function auditQuery(filters: AuditFilters, before: string | undefined) {
  return {
    agent: filters.agent,
    status: filters.status,
    threadId: filters.threadId,
    since: sinceFor(filters.window),
    limit: PAGE_SIZE,
    before,
  };
}

/** Loading, error, empty and "Load more" around a list. */
export function ListFrame({
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

export function Th({ children }: { children: ReactNode }) {
  return (
    <th scope="col" className="px-3 py-2 font-medium">
      {children}
    </th>
  );
}

export function Td({ children }: { children: ReactNode }) {
  return <td className="px-3 py-2 align-middle">{children}</td>;
}

export function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="min-w-0">
      <dt className="text-xs text-muted">{label}</dt>
      <dd className="break-words">{children}</dd>
    </div>
  );
}

export function ThreadLink({ threadId }: { threadId: string }) {
  return (
    <Link
      to={`/prompts/${threadId}?panel=trace`}
      className="inline-flex min-h-11 items-center text-accent underline"
    >
      <span className="sr-only">Thread </span>
      <code>{shortId(threadId)}</code>
    </Link>
  );
}

export function agentName(names: AgentNames, agentId: string): string {
  return names.get(agentId) ?? shortId(agentId);
}

export function tokensInOut(c: LlmCallDto): string {
  if (!c.usageKnown) return 'unknown';
  return `${formatTokens(c.promptTokens)} / ${formatTokens(c.completionTokens)}`;
}

export function estimated(c: LlmCallDto): string {
  return c.estimatedPromptTokens === null || c.estimatedPromptTokens === undefined
    ? '—'
    : formatTokens(c.estimatedPromptTokens);
}
