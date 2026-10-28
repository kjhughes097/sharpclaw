import { useState } from 'react';
import { Link } from 'react-router';
import { useAgents } from '../../api/hooks';
import { budgetResetLocalTime, errorMessage } from '../../api/errors';
import type { AgentDto } from '../../api/types';
import { useToast } from '../../app/toast';
import { Button, EmptyState, ErrorState, LoadingState, Meter } from '../../components/ui';
import { formatUsd } from '../../lib/time';
import { ConfigNote, EnabledSwitch, LastSeen } from './AgentParts';
import { useUpdateAgent } from './agentQueries';
import { CreateAgentDialog } from './CreateAgentDialog';
import { TokenRevealDialog } from './TokenRevealDialog';

export function AgentsPage() {
  const agents = useAgents();
  const [creating, setCreating] = useState(false);
  // The new token's value: held only while the reveal dialog is open, then cleared.
  const [revealed, setRevealed] = useState<string | null>(null);

  return (
    <div className="min-h-0 flex-1 overflow-y-auto">
      <div className="mx-auto max-w-5xl space-y-4 p-4">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h1 className="text-xl font-semibold">Agents</h1>
          <Button variant="primary" onClick={() => setCreating(true)}>
            New agent
          </Button>
        </div>
        <ConfigNote />
        {agents.isPending ? (
          <LoadingState label="Loading agents" />
        ) : agents.isError ? (
          <ErrorState message={errorMessage(agents.error)} onRetry={() => void agents.refetch()} />
        ) : agents.data.length === 0 ? (
          <EmptyState title="No agents yet. Create one to get a token for its container." />
        ) : (
          <ul className="grid gap-3 md:grid-cols-2">
            {agents.data.map((agent) => (
              <AgentCard key={agent.id} agent={agent} />
            ))}
          </ul>
        )}
      </div>
      <CreateAgentDialog
        open={creating}
        onOpenChange={setCreating}
        onCreated={(value) => {
          setCreating(false);
          setRevealed(value);
        }}
      />
      <TokenRevealDialog value={revealed} onClose={() => setRevealed(null)} />
    </div>
  );
}

function AgentCard({ agent }: { agent: AgentDto }) {
  const update = useUpdateAgent();
  const toast = useToast();

  return (
    <li className="space-y-3 rounded-lg border border-border bg-surface p-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="min-w-0 text-base font-semibold break-all">
          <Link
            to={`/agents/${encodeURIComponent(agent.name)}`}
            className="text-accent underline-offset-2 hover:underline"
          >
            {agent.name}
          </Link>
        </h2>
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
      <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm">
        <dt className="text-muted">Model</dt>
        <dd className="break-all">{agent.model}</dd>
        <dt className="text-muted">Context window</dt>
        <dd>{agent.contextWindowTokens ?? 'default'}</dd>
        <dt className="text-muted">Last seen</dt>
        <dd>
          <LastSeen iso={agent.lastSeenAt} />
        </dd>
      </dl>
      <div className="space-y-1">
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
      </div>
    </li>
  );
}
