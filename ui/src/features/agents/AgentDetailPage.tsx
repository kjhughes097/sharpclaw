import { Link, useParams } from 'react-router';
import { ApiError, budgetResetLocalTime, errorMessage } from '../../api/errors';
import type { AgentDto } from '../../api/types';
import { useToast } from '../../app/toast';
import { ErrorState, LoadingState, Meter } from '../../components/ui';
import { formatUsd } from '../../lib/time';
import { ConfigNote, EnabledSwitch, LastSeen } from './AgentParts';
import { useAgent, useUpdateAgent } from './agentQueries';
import { SettingsForm } from './AgentSettingsForm';
import { TokensSection } from './AgentTokens';
import { LlmCallsSection } from './AgentLlmCalls';

export function AgentDetailPage() {
  const { name = '' } = useParams();
  const agent = useAgent(name);

  return (
    <div className="min-h-0 flex-1 overflow-y-auto">
      <div className="mx-auto max-w-5xl space-y-6 p-4">
        <Link to="/agents" className="inline-flex min-h-11 items-center text-sm text-accent underline">
          Back to agents
        </Link>
        {agent.isPending ? (
          <LoadingState label="Loading agent" />
        ) : agent.isError ? (
          agent.error instanceof ApiError && agent.error.status === 404 ? (
            <div role="alert" className="rounded-lg border border-border p-4">
              <h1 className="text-xl font-semibold">Agent not found</h1>
              <p className="mt-1 text-sm">Not found. It may have been removed.</p>
            </div>
          ) : (
            <ErrorState message={errorMessage(agent.error)} onRetry={() => void agent.refetch()} />
          )
        ) : (
          <AgentDetail agent={agent.data} />
        )}
      </div>
    </div>
  );
}

function AgentDetail({ agent }: { agent: AgentDto }) {
  const update = useUpdateAgent();
  const toast = useToast();

  return (
    <>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="min-w-0 text-xl font-semibold break-all">{agent.name}</h1>
        <EnabledSwitch
          checked={agent.enabled}
          label={`Enabled: ${agent.name}`}
          disabled={update.isPending}
          onChange={(enabled) =>
            update.mutate(
              { name: agent.name, changes: { enabled } },
              { onError: (err) => toast(errorMessage(err), 'error') },
            )
          }
        />
      </div>
      <ConfigNote />
      <section
        aria-labelledby="agent-status"
        className="space-y-2 rounded-lg border border-border bg-surface p-4"
      >
        <h2 id="agent-status" className="font-semibold">
          Status
        </h2>
        <p className="text-sm">
          <span className="text-muted">Last seen: </span>
          <LastSeen iso={agent.lastSeenAt} />
        </p>
        <p className="text-sm">
          <span className="text-muted">Today: </span>
          {formatUsd(agent.todaySpendUsd)} of {formatUsd(agent.dailyBudgetUsd)}
        </p>
        <Meter
          value={agent.todaySpendUsd}
          max={agent.dailyBudgetUsd}
          label={`Today's spend for ${agent.name}`}
        />
        <p className="text-xs text-muted">resets at 00:00 UTC ({budgetResetLocalTime()} local)</p>
      </section>
      {/* Keyed so the form picks up new values after a save or an external change. */}
      <SettingsForm
        key={`${agent.model}|${agent.dailyBudgetUsd}|${agent.contextWindowTokens ?? ''}`}
        agent={agent}
      />
      <TokensSection name={agent.name} />
      <LlmCallsSection name={agent.name} />
    </>
  );
}
