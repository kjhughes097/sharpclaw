import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { Link } from 'react-router';
import { api, call } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { keys } from '../../api/queryKeys';
import type { AgentDto, ApprovalGrantDto } from '../../api/types';
import { useToast } from '../../app/toast';
import { Badge, Button, ConfirmDialog, ErrorState, RelativeTime, Spinner } from '../../components/ui';
import { formatAbsolute } from '../../lib/time';
import { useAgentGrants, useUpdateAgent } from './agentQueries';

/**
 * How much this agent may do without asking (3.6 spec section 8): the autonomy switch and the
 * standing approvals made from approval cards, each revocable.
 */
export function ApprovalPolicySection({ agent }: { agent: AgentDto }) {
  const id = useId();
  return (
    <section
      aria-labelledby={`${id}-title`}
      className="space-y-3 rounded-lg border border-border bg-surface p-4"
    >
      <h2 id={`${id}-title`} className="font-semibold">
        Approvals
      </h2>
      <AutonomySwitch agent={agent} />
      <GrantsList name={agent.name} />
    </section>
  );
}

function AutonomySwitch({ agent }: { agent: AgentDto }) {
  const update = useUpdateAgent();
  const toast = useToast();
  const [confirming, setConfirming] = useState(false);
  const set = (autonomous: boolean) =>
    update.mutate(
      { name: agent.name, changes: { autonomous } },
      {
        onSuccess: () => toast(autonomous ? 'Autonomy mode on.' : 'Autonomy mode off.', 'success'),
        onError: (err) => toast(errorMessage(err), 'error'),
        onSettled: () => setConfirming(false),
      },
    );

  return (
    <div className="flex flex-wrap items-center gap-3">
      <span className="flex items-center gap-2">
        <span className="font-medium">Autonomy mode</span>
        <Badge tone={agent.autonomous ? 'warn' : 'neutral'}>{agent.autonomous ? 'on' : 'off'}</Badge>
      </span>
      <span className="text-sm text-muted">
        {agent.autonomous
          ? 'Every tool call this agent makes is approved without asking.'
          : 'Calls above the agent’s risk level ask you first.'}
      </span>
      <Button
        className="ml-auto"
        busy={update.isPending && !confirming}
        onClick={() => (agent.autonomous ? set(false) : setConfirming(true))}
      >
        {agent.autonomous ? 'Turn off' : 'Turn on'}
      </Button>
      <ConfirmDialog
        open={confirming}
        onOpenChange={setConfirming}
        title={`Turn on autonomy mode for ${agent.name}?`}
        description="The gateway will approve every tool call this agent makes, at any risk, including GitHub comments and pull requests as you. Blocked calls still never run, and the daily budget still applies."
        confirmLabel="Turn on"
        danger
        busy={update.isPending}
        onConfirm={() => set(true)}
      />
    </div>
  );
}

function scopeText(grant: ApprovalGrantDto) {
  if (grant.scope !== 'thread' || !grant.threadId) return 'all threads';
  return (
    <Link to={`/prompts/${grant.threadId}`} className="text-accent underline">
      one thread
    </Link>
  );
}

function GrantsList({ name }: { name: string }) {
  const grants = useAgentGrants(name);
  const queryClient = useQueryClient();
  const toast = useToast();
  const revoke = useMutation({
    mutationFn: (grantId: string) =>
      call(() =>
        api.DELETE('/api/agents/{idOrName}/grants/{grantId}', {
          params: { path: { idOrName: name, grantId } },
        }),
      ),
    onSuccess: () => toast('Standing approval revoked.', 'success'),
    onError: (err) => toast(errorMessage(err), 'error'),
    onSettled: () => queryClient.invalidateQueries({ queryKey: keys.grants(name) }),
  });

  if (grants.isPending) return <Spinner label="Loading standing approvals" />;
  if (grants.isError)
    return <ErrorState message={errorMessage(grants.error)} onRetry={() => void grants.refetch()} />;
  if (grants.data.length === 0)
    return (
      <p className="text-sm text-muted">
        No standing approvals. Use “Approve and remember” on an approval to stop being asked about a tool.
      </p>
    );
  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[32rem] text-left text-sm">
        <caption className="sr-only">Standing approvals for {name}</caption>
        <thead className="text-muted">
          <tr>
            <th scope="col" className="py-2 pr-3 font-medium">
              Tool
            </th>
            <th scope="col" className="py-2 pr-3 font-medium">
              Where
            </th>
            <th scope="col" className="py-2 pr-3 font-medium">
              Until
            </th>
            <th scope="col" className="py-2 pr-3 font-medium">
              Granted
            </th>
            <th scope="col" className="py-2 font-medium">
              <span className="sr-only">Actions</span>
            </th>
          </tr>
        </thead>
        <tbody>
          {grants.data.map((grant) => (
            <tr key={grant.id} className="border-t border-border">
              <td className="py-2 pr-3 font-mono">{grant.toolName}</td>
              <td className="py-2 pr-3">{scopeText(grant)}</td>
              <td className="py-2 pr-3">
                {grant.expiresAt ? formatAbsolute(grant.expiresAt) : 'until revoked'}
              </td>
              <td className="py-2 pr-3">
                <RelativeTime iso={grant.createdAt} />
                {grant.createdByUser &&
                  ` by ${grant.createdByUser === 'admin_key' ? 'admin key' : grant.createdByUser}`}
              </td>
              <td className="py-2 text-right">
                <Button
                  variant="ghost"
                  busy={revoke.isPending && revoke.variables === grant.id}
                  aria-label={`Revoke standing approval for ${grant.toolName}`}
                  onClick={() => revoke.mutate(grant.id)}
                >
                  Revoke
                </Button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
