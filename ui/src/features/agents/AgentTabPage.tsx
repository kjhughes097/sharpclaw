import { useQuery } from '@tanstack/react-query';
import { useId, useState, type ReactNode } from 'react';
import { Link, useParams } from 'react-router';
import { api, call } from '../../api/client';
import { ApiError, budgetResetLocalTime, errorMessage } from '../../api/errors';
import { useUsage } from '../../api/hooks';
import { keys } from '../../api/queryKeys';
import type { AgentDto } from '../../api/types';
import { useToast } from '../../app/toast';
import { CONNECTION_DOT } from '../../components/ShellParts';
import {
  Badge,
  Button,
  ConfirmDialog,
  ErrorState,
  LoadingState,
  Meter,
  RelativeTime,
  Spinner,
} from '../../components/ui';
import { formatUsd, useServerNow } from '../../lib/time';
import { ApprovalCard } from '../approvals/ApprovalCard';
import { ActivityList, useActivity } from '../overview/ActivityList';
import { isOnline } from '../overview/OverviewPage';
import { CostByModelCard, UsageCard } from '../overview/UsageCards';
import { useLiveActivity } from '../overview/useLiveActivity';
import { ApprovalPolicySection } from './AgentApprovals';
import { ConfigNote, LastSeen } from './AgentParts';
import { useAgent, useUpdateAgent } from './agentQueries';
import { LlmCallsSection } from './AgentLlmCalls';
import { SettingsForm } from './AgentSettingsForm';
import { SkillsSection } from './AgentSkills';
import { TokensSection } from './AgentTokens';

const RECENT_SESSIONS = 10;

/** `/agents/:name`: everything about one agent (3.3.1 spec section 7). */
export function AgentTabPage() {
  const { name = '' } = useParams();
  const agent = useAgent(name);

  return (
    <div className="relative min-h-0 flex-1 overflow-y-auto">
      <div className="space-y-2.5 p-2.5">
        {agent.isPending ? (
          <LoadingState label="Loading agent" />
        ) : agent.isError ? (
          agent.error instanceof ApiError && agent.error.status === 404 ? (
            <div role="alert" className="rounded-md border border-border p-4">
              <h1 className="text-xl font-semibold">Agent not found</h1>
              <p className="mt-1 text-sm">
                Not found. It may have been removed.{' '}
                <Link to="/agents" className="text-accent underline">
                  All agents
                </Link>
              </p>
            </div>
          ) : (
            <ErrorState message={errorMessage(agent.error)} onRetry={() => void agent.refetch()} />
          )
        ) : (
          <AgentTab agent={agent.data} />
        )}
      </div>
    </div>
  );
}

function Section({ title, action, children }: { title: string; action?: ReactNode; children: ReactNode }) {
  const id = useId();
  return (
    <section aria-labelledby={id} className="rounded-md border border-border bg-surface p-2.5">
      <div className="mb-1.5 flex items-center justify-between gap-2">
        <h2 id={id} className="text-sm font-semibold">
          {title}
        </h2>
        {action}
      </div>
      {children}
    </section>
  );
}

function AgentTab({ agent }: { agent: AgentDto }) {
  useLiveActivity();
  const today = useUsage('today', agent.name);
  const week = useUsage('7d', agent.name);
  const month = useUsage('30d', agent.name);
  const activity = useActivity(agent.name);

  return (
    <>
      <HeaderStrip agent={agent} />
      <div className="grid grid-cols-1 gap-2.5 sm:grid-cols-2 xl:grid-cols-4">
        <UsageCard range="today" usage={today} />
        <UsageCard range="7d" usage={week} />
        <UsageCard range="30d" usage={month} />
        <CostByModelCard usage={week} />
      </div>
      <div className="grid grid-cols-1 gap-2.5 lg:grid-cols-[2fr_1fr]">
        <Section title="Activity">
          <ActivityList activity={activity} heightClass="max-h-[28rem]" />
        </Section>
        <div className="space-y-2.5">
          <RecentSessions agent={agent} />
          <PendingApprovals name={agent.name} />
        </div>
      </div>
      <ConfigNote />
      {/* Keyed so the form picks up new values after a save or an external change. */}
      <SettingsForm
        key={`${agent.model}|${agent.dailyBudgetUsd}|${agent.contextWindowTokens ?? ''}`}
        agent={agent}
      />
      <ApprovalPolicySection agent={agent} />
      <SkillsSection agent={agent} />
      <TokensSection name={agent.name} />
      <LlmCallsSection name={agent.name} />
    </>
  );
}

function HeaderStrip({ agent }: { agent: AgentDto }) {
  const update = useUpdateAgent();
  const toast = useToast();
  const [confirming, setConfirming] = useState(false);
  const now = useServerNow();
  const online = isOnline(agent, now);
  const setEnabled = (enabled: boolean) =>
    update.mutate(
      { name: agent.name, changes: { enabled } },
      { onError: (err) => toast(errorMessage(err), 'error'), onSettled: () => setConfirming(false) },
    );

  return (
    <section
      aria-label="Agent"
      className="flex flex-wrap items-center gap-x-5 gap-y-2 rounded-md border border-border bg-surface p-2.5 text-sm"
    >
      <span className="flex items-center gap-2">
        <h1 className="text-lg font-semibold">{agent.name}</h1>
        <Badge tone={agent.enabled ? 'ok' : 'neutral'}>{agent.enabled ? 'enabled' : 'disabled'}</Badge>
        {agent.autonomous && <Badge tone="warn">autonomous</Badge>}
      </span>
      <span className="text-muted">{agent.model}</span>
      <span className="inline-flex items-center gap-1.5">
        <span
          className={`h-2 w-2 rounded-full ${online ? CONNECTION_DOT.ok : CONNECTION_DOT.off}`}
          aria-hidden="true"
        />
        <span className="text-muted">Last seen</span> <LastSeen iso={agent.lastSeenAt} />
      </span>
      <span>
        <span className="text-muted">Context window</span> {agent.contextWindowTokens ?? 'default'}
      </span>
      <span className="inline-flex min-w-48 flex-col">
        <span>
          <span className="text-muted">Today</span> {formatUsd(agent.todaySpendUsd)} /{' '}
          {formatUsd(agent.dailyBudgetUsd)}
        </span>
        <Meter
          value={agent.todaySpendUsd}
          max={agent.dailyBudgetUsd}
          label={`Today's spend for ${agent.name}`}
        />
        <span className="text-xs text-muted">resets at 00:00 UTC ({budgetResetLocalTime()} local)</span>
      </span>
      <span className="ml-auto flex gap-2">
        <Link
          to={`/prompts?agent=${encodeURIComponent(agent.name)}`}
          className="inline-flex min-h-11 items-center rounded-md bg-accent px-4 text-sm font-medium text-accent-fg"
        >
          Open in Prompts
        </Link>
        <Button
          busy={update.isPending && !confirming}
          onClick={() => (agent.enabled ? setConfirming(true) : setEnabled(true))}
        >
          {agent.enabled ? 'Disable' : 'Enable'}
        </Button>
      </span>
      <ConfirmDialog
        open={confirming}
        onOpenChange={setConfirming}
        title={`Disable ${agent.name}?`}
        description="Messages to this agent will queue until it is enabled again."
        confirmLabel="Disable"
        danger
        busy={update.isPending}
        onConfirm={() => setEnabled(false)}
      />
    </section>
  );
}

function RecentSessions({ agent }: { agent: AgentDto }) {
  const threads = useQuery({
    queryKey: [...keys.threads, 'recent', agent.id] as const,
    queryFn: () =>
      call(() =>
        api.GET('/api/threads', { params: { query: { agentId: agent.id, limit: RECENT_SESSIONS } } }),
      ),
    select: (data) => data.threads,
  });
  return (
    <Section
      title="Sessions"
      action={
        <Link
          to={`/prompts?agent=${encodeURIComponent(agent.name)}`}
          className="text-xs text-accent underline"
        >
          View all
        </Link>
      }
    >
      {threads.isPending ? (
        <Spinner label="Loading sessions" />
      ) : threads.isError ? (
        <ErrorState message={errorMessage(threads.error)} onRetry={() => void threads.refetch()} />
      ) : threads.data.length === 0 ? (
        <p className="text-sm text-muted">No sessions yet.</p>
      ) : (
        <ul className="space-y-0.5 text-sm">
          {threads.data.map((t) => (
            <li key={t.id}>
              <Link
                to={`/prompts/${t.id}`}
                className="flex items-center gap-2 rounded px-1 py-1 hover:bg-surface-2"
              >
                <span className="min-w-0 flex-1 truncate">{t.title || 'Untitled session'}</span>
                {t.inFlight && <Badge tone="accent">replying</Badge>}
                {t.pendingApprovals > 0 && (
                  <Badge tone="danger">
                    {t.pendingApprovals} approval{t.pendingApprovals === 1 ? '' : 's'}
                  </Badge>
                )}
                {t.queued > 0 && <Badge tone="warn">{t.queued} queued</Badge>}
                <span className="shrink-0 text-xs text-muted">
                  <RelativeTime iso={t.lastMessageAt} />
                </span>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </Section>
  );
}

function PendingApprovals({ name }: { name: string }) {
  const approvals = useQuery({
    // Under the approvals prefix, so approval events refresh it.
    queryKey: keys.approvalList(`pending:${name}`),
    queryFn: () =>
      call(() =>
        api.GET('/api/approvals', { params: { query: { status: 'pending', agent: name, limit: 50 } } }),
      ),
    select: (data) => data.approvals,
  });
  return (
    <Section title="Pending approvals">
      {approvals.isPending ? (
        <Spinner label="Loading approvals" />
      ) : approvals.isError ? (
        <ErrorState message={errorMessage(approvals.error)} onRetry={() => void approvals.refetch()} />
      ) : approvals.data.length === 0 ? (
        <p className="text-sm text-muted">Nothing waiting.</p>
      ) : (
        <ul className="space-y-2">
          {approvals.data.map((a) => (
            <li key={a.id}>
              <ApprovalCard approval={a} showThreadLink />
            </li>
          ))}
        </ul>
      )}
    </Section>
  );
}
