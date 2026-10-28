import { useId } from 'react';
import { Link } from 'react-router';
import { errorMessage } from '../../api/errors';
import { Badge, EmptyState, ErrorState, LoadingState, RelativeTime } from '../../components/ui';
import { formatCost, formatDuration } from '../../lib/time';
import { useAgentLlmCalls } from './agentQueries';

export function LlmCallsSection({ name }: { name: string }) {
  const id = useId();
  const calls = useAgentLlmCalls(name);

  return (
    <section
      aria-labelledby={`${id}-title`}
      className="space-y-3 rounded-lg border border-border bg-surface p-4"
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id={`${id}-title`} className="font-semibold">
          Recent LLM calls
        </h2>
        <Link
          to={`/audit?tab=llm&agent=${encodeURIComponent(name)}`}
          className="inline-flex min-h-11 items-center text-sm text-accent underline"
        >
          View in Audit
        </Link>
      </div>
      {calls.isPending ? (
        <LoadingState label="Loading LLM calls" />
      ) : calls.isError ? (
        <ErrorState message={errorMessage(calls.error)} onRetry={() => void calls.refetch()} />
      ) : calls.data.length === 0 ? (
        <EmptyState title="No LLM calls yet." />
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[40rem] text-left text-sm">
            <caption className="sr-only">The last 20 LLM calls for {name}</caption>
            <thead className="text-muted">
              <tr>
                <th scope="col" className="py-2 pr-3 font-medium">
                  Time
                </th>
                <th scope="col" className="py-2 pr-3 font-medium">
                  Model
                </th>
                <th scope="col" className="py-2 pr-3 font-medium">
                  Status
                </th>
                <th scope="col" className="py-2 pr-3 font-medium">
                  Tokens in/out
                </th>
                <th scope="col" className="py-2 pr-3 font-medium">
                  Cost
                </th>
                <th scope="col" className="py-2 font-medium">
                  Latency
                </th>
              </tr>
            </thead>
            <tbody>
              {calls.data.map((c) => (
                <tr key={c.id} className="border-t border-border">
                  <td className="py-2 pr-3">
                    <RelativeTime iso={c.createdAt} />
                  </td>
                  <td className="py-2 pr-3 break-all">{c.model}</td>
                  <td className="py-2 pr-3">
                    <Badge
                      tone={
                        c.status === 'ok'
                          ? 'ok'
                          : c.status === 'cancelled'
                            ? 'neutral'
                            : c.status === 'rejected'
                              ? 'warn'
                              : 'danger'
                      }
                    >
                      {c.status}
                    </Badge>
                  </td>
                  <td className="py-2 pr-3">
                    {c.promptTokens} / {c.completionTokens}
                  </td>
                  <td className="py-2 pr-3">{formatCost(c.costUsd)}</td>
                  <td className="py-2">{formatDuration(c.latencyMs)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
