import { useState } from 'react';
import { Link } from 'react-router';
import { useAgents } from '../../api/hooks';
import { budgetResetLocalTime, errorMessage } from '../../api/errors';
import type { AgentDto } from '../../api/types';
import { useToast } from '../../app/toast';
import { Button, EmptyState, ErrorState, LoadingState, Meter, RelativeTime } from '../../components/ui';
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
          <>
            <div className="overflow-x-auto rounded-md border border-border bg-surface">
              <table className="w-full min-w-[48rem] text-left text-sm">
                <caption className="sr-only">Agents</caption>
                <thead className="text-xs text-muted">
                  <tr>
                    {['Name', 'Model', 'Enabled', 'Today', 'Context window', 'Last seen', 'Created'].map(
                      (h) => (
                        <th key={h} scope="col" className="px-3 py-2 font-medium">
                          {h}
                        </th>
                      ),
                    )}
                  </tr>
                </thead>
                <tbody>
                  {agents.data.map((agent) => (
                    <AgentRow key={agent.id} agent={agent} />
                  ))}
                </tbody>
              </table>
            </div>
            <p className="text-xs text-muted">Budgets reset at 00:00 UTC ({budgetResetLocalTime()} local).</p>
          </>
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

/** One row of the admin table (3.3.1 spec section 8.3); the name opens the agent's tab. */
function AgentRow({ agent }: { agent: AgentDto }) {
  const update = useUpdateAgent();
  const toast = useToast();

  return (
    <tr className="border-t border-border align-top">
      <th scope="row" className="px-3 py-2 font-semibold break-all">
        <Link
          to={`/agents/${encodeURIComponent(agent.name)}`}
          className="text-accent underline-offset-2 hover:underline"
        >
          {agent.name}
        </Link>
      </th>
      <td className="px-3 py-2 break-all">{agent.model}</td>
      <td className="px-3 py-1">
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
      </td>
      <td className="min-w-40 px-3 py-2">
        <p>
          {formatUsd(agent.todaySpendUsd)} of {formatUsd(agent.dailyBudgetUsd)}
        </p>
        <Meter
          value={agent.todaySpendUsd}
          max={agent.dailyBudgetUsd}
          label={`Today's spend for ${agent.name}`}
        />
      </td>
      <td className="px-3 py-2">{agent.contextWindowTokens ?? 'default'}</td>
      <td className="px-3 py-2">
        <LastSeen iso={agent.lastSeenAt} />
      </td>
      <td className="px-3 py-2 whitespace-nowrap">
        <RelativeTime iso={agent.createdAt} />
      </td>
    </tr>
  );
}
