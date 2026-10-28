import { Link } from 'react-router';
import { useAgents, useUsage } from '../../api/hooks';
import { errorMessage } from '../../api/errors';
import type { AgentDto, UsageRange, UsageResponse } from '../../api/types';
import { ErrorState, LoadingState } from '../../components/ui';
import { CONNECTION_DOT, useConnectionLabel } from '../../components/AppShell';
import { formatCount, formatMoney, serverNow, useServerNow } from '../../lib/time';
import { ActivityList, useActivity } from './ActivityList';
import { CostByModelCard, CreditsCard, RANGES, UsageCard, totalTokens } from './UsageCards';
import { useLiveActivity } from './useLiveActivity';

/** An agent counts as online when it called the gateway within this window. */
export const ONLINE_WINDOW_MS = 3 * 60_000;

export function isOnline(agent: AgentDto, now = serverNow()): boolean {
  return !!agent.lastSeenAt && now - Date.parse(agent.lastSeenAt) <= ONLINE_WINDOW_MS;
}

const USAGE_POLL_MS = 60_000;

function LiveBar({ live }: { live: ReturnType<typeof useLiveActivity> }) {
  const connection = useConnectionLabel();
  return (
    <div className="flex items-center justify-between border-b border-border px-3 py-1 text-xs">
      <span className="inline-flex items-center gap-1.5">
        {live.paused ? (
          <>Paused{live.missed > 0 && ` · ${live.missed} new`}</>
        ) : (
          <>
            Live messages
            <span
              className={`h-2 w-2 rounded-full ${CONNECTION_DOT[connection.tone]}`}
              title={connection.label}
              aria-hidden="true"
            />
          </>
        )}
      </span>
      <button
        type="button"
        onClick={live.paused ? live.resume : live.pause}
        className="min-h-8 rounded border border-border px-3 hover:bg-surface-2"
      >
        {live.paused ? 'resume' : 'pause'}
      </button>
    </div>
  );
}

type Usage = Partial<Record<UsageRange, UsageResponse | undefined>>;

function AgentCard({ agent, usage }: { agent: AgentDto; usage: Usage }) {
  const activity = useActivity(agent.name);
  const now = useServerNow();
  const online = isOnline(agent, now);
  // A claimed prompt in the feed means a turn is in flight.
  const busy =
    activity.data?.pages[0]?.items.some((i) => i.role === 'user' && i.status === 'claimed') ?? false;

  return (
    <section
      aria-label={`Agent ${agent.name}`}
      className={`flex min-w-0 flex-col rounded-md border border-border bg-surface p-2.5 ${agent.enabled ? '' : 'opacity-60'}`}
    >
      <div className="flex items-baseline justify-between gap-2">
        <h2 className="flex items-center gap-1.5 font-semibold">
          <Link to={`/agents/${encodeURIComponent(agent.name)}`} className="hover:underline">
            {agent.name}
          </Link>
          <span
            className={`h-2 w-2 rounded-full ${online ? CONNECTION_DOT.ok : CONNECTION_DOT.off}`}
            title={online ? 'online' : 'offline'}
          />
          <span className="sr-only">{online ? 'online' : 'offline'}</span>
          {busy && (
            <>
              <span className={`h-2 w-2 rounded-full ${CONNECTION_DOT.warn}`} title="replying" />
              <span className="sr-only">replying</span>
            </>
          )}
          {!agent.enabled && <span className="text-xs font-normal text-muted">disabled</span>}
        </h2>
        <span className="truncate text-sm text-muted">{agent.model}</span>
      </div>
      <dl className="mt-1 font-mono text-xs">
        {(['today', '7d', '30d'] as const).map((range) => {
          const row = usage[range]?.byAgent.find((a) => a.agentId === agent.id);
          return (
            <div key={range} className="flex justify-between">
              <dt className="text-muted">{RANGES[range].title}</dt>
              <dd>{row ? `${formatCount(totalTokens(row))} tokens ${formatMoney(row.costUsd)}` : '—'}</dd>
            </div>
          );
        })}
      </dl>
      <h3 className="mt-2 border-b border-border pb-0.5 text-[0.65rem] tracking-wider text-muted uppercase">
        Messages
      </h3>
      <ActivityList activity={activity} />
    </section>
  );
}

/**
 * `/overview` (3.3.1 spec section 5): usage cards, cost by model, credits, then a card per agent
 * with its spend and latest messages, all live from the event stream unless paused.
 */
export function OverviewPage() {
  const live = useLiveActivity();
  const poll = live.paused ? false : USAGE_POLL_MS;
  const today = useUsage('today', null, poll);
  const week = useUsage('7d', null, poll);
  const month = useUsage('30d', null, poll);
  const agents = useAgents();
  const usage: Usage = { today: today.data, '7d': week.data, '30d': month.data };
  const sorted = [...(agents.data ?? [])].sort(
    (a, b) => Number(b.enabled) - Number(a.enabled) || a.name.localeCompare(b.name),
  );

  return (
    <div className="relative min-h-0 flex-1 overflow-y-auto">
      <h1 className="sr-only">Overview</h1>
      <LiveBar live={live} />
      <div className="space-y-2.5 p-2.5">
        <div className="grid grid-cols-1 gap-2.5 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-5">
          <UsageCard range="today" usage={today} />
          <UsageCard range="7d" usage={week} />
          <UsageCard range="30d" usage={month} />
          <CostByModelCard usage={week} />
          <CreditsCard />
        </div>
        {agents.isPending ? (
          <LoadingState label="Loading agents" />
        ) : agents.isError ? (
          <ErrorState message={errorMessage(agents.error)} onRetry={() => void agents.refetch()} />
        ) : sorted.length === 0 ? (
          <p className="p-4 text-sm text-muted">
            No agents yet.{' '}
            <Link to="/agents" className="text-accent underline">
              Create one
            </Link>
            .
          </p>
        ) : (
          <div className="grid grid-cols-1 gap-2.5 md:grid-cols-2">
            {sorted.map((agent) => (
              <AgentCard key={agent.id} agent={agent} usage={usage} />
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
