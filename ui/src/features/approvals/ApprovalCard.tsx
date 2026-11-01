import { useMutation, useQueryClient } from '@tanstack/react-query';
import { DropdownMenu } from 'radix-ui';
import { useState } from 'react';
import { Link } from 'react-router';
import { api, call } from '../../api/client';
import { ApiError, errorMessage } from '../../api/errors';
import { keys } from '../../api/queryKeys';
import { GRANT_TTL_HOURS, HIGHEST_RISK, NON_GRANTABLE_TOOLS } from '../../api/types';
import { useToast } from '../../app/toast';
import { Badge, Button, RelativeTime, RiskBadge } from '../../components/ui';
import { formatCountdown, useServerNow } from '../../lib/time';
import { ApprovalDetail, hasApprovalDetail } from './ApprovalDetail';
import { deciderLabel } from './decider';

export interface ApprovalView {
  id: string;
  toolName: string;
  risk: string;
  summary: string;
  reason?: string | null;
  status: string;
  createdAt: string;
  expiresAt: string;
  threadId?: string;
  agent?: string;
  decidedBy?: string | null;
  decidedByUser?: string | null;
}

/** "Approve and don't ask again": a standing approval for this tool (3.6 spec section 7). */
export interface Remember {
  scope: 'thread' | 'agent';
  ttlHours?: number;
}

/** Decides an approval; a 409 (already decided or expired) shows a notice and refreshes. */
export function useDecideApproval(threadId?: string) {
  const queryClient = useQueryClient();
  const toast = useToast();
  return useMutation({
    mutationFn: ({
      id,
      decision,
      remember,
    }: {
      id: string;
      decision: 'approved' | 'denied';
      remember?: Remember;
    }) =>
      call(() =>
        api.POST('/api/approvals/{id}', {
          params: { path: { id } },
          body: remember
            ? { decision, remember: { scope: remember.scope, ttlHours: remember.ttlHours ?? null } }
            : { decision },
        }),
      ),
    onError: (error) => {
      const conflict = error instanceof ApiError && error.code === 'conflict';
      toast(conflict ? 'Already decided.' : errorMessage(error), conflict ? 'info' : 'error');
    },
    onSuccess: (_data, { remember }) => {
      if (remember) {
        void queryClient.invalidateQueries({ queryKey: keys.allGrants });
        toast(`Approved. ${rememberLabel(remember)} won't ask again.`, 'success');
      }
    },
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: keys.approvals });
      if (threadId) void queryClient.invalidateQueries({ queryKey: keys.thread(threadId) });
    },
  });
}

function rememberLabel(remember: Remember): string {
  const where = remember.scope === 'thread' ? 'This tool in this thread' : 'This tool for this agent';
  return remember.ttlHours ? `${where} (for ${remember.ttlHours} h)` : where;
}

const REMEMBER_OPTIONS: { label: string; remember: Remember }[] = [
  { label: 'Approve for this thread', remember: { scope: 'thread' } },
  { label: 'Approve for this agent', remember: { scope: 'agent' } },
  ...GRANT_TTL_HOURS.map((hours) => ({
    label: `Approve for this agent for ${hours} h`,
    remember: { scope: 'agent' as const, ttlHours: hours },
  })),
];

const MENU_ITEM =
  'flex min-h-11 cursor-pointer items-center rounded px-3 text-sm outline-none data-[highlighted]:bg-surface-2';

const STATUS_TEXT: Record<string, string> = {
  approved: 'Approved',
  denied: 'Denied',
  expired: 'Expired — the agent was told',
};

/**
 * One approval: tool, risk, summary, reason, countdown and Approve / Deny, plus a menu to approve
 * and stop asking for this tool (not offered for tools that act as the user). The highest risk
 * level needs a second confirmation (3.3 spec section 9.4).
 */
export function ApprovalCard({
  approval,
  showThreadLink = false,
}: {
  approval: ApprovalView;
  showThreadLink?: boolean;
}) {
  const decide = useDecideApproval(approval.threadId);
  // Ticks every second, and re-renders as soon as the server clock is known.
  const now = useServerNow(1000);
  // Set while the critical-risk confirmation is showing; holds the remember choice, if any.
  const [confirming, setConfirming] = useState<{ remember?: Remember } | null>(null);
  const due = Date.parse(approval.expiresAt) <= now;
  const pending = approval.status === 'pending' && !due;
  const critical = approval.risk === HIGHEST_RISK;
  const status = approval.status === 'pending' && due ? 'expired' : approval.status;

  const grantable = !NON_GRANTABLE_TOOLS.includes(approval.toolName);

  const approve = (remember?: Remember) => {
    if (critical && !confirming) {
      setConfirming(remember ? { remember } : {});
      return;
    }
    decide.mutate({ id: approval.id, decision: 'approved', remember: confirming?.remember ?? remember });
    setConfirming(null);
  };

  return (
    <section
      aria-label={`Approval for ${approval.toolName}`}
      className={`rounded-lg border p-3 ${pending ? 'border-warn bg-warn-soft' : 'border-border bg-surface opacity-80'}`}
    >
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-medium">{approval.toolName}</span>
        <RiskBadge risk={approval.risk} />
        {approval.agent && <Badge>{approval.agent}</Badge>}
        {pending ? (
          <span
            className="ml-auto text-xs text-muted"
            aria-label={`Expires in ${formatCountdown(approval.expiresAt, now)}`}
          >
            expires in {formatCountdown(approval.expiresAt, now)}
          </span>
        ) : (
          <span className="ml-auto text-xs text-muted">
            {STATUS_TEXT[status] ?? status}
            {(status === 'approved' || status === 'denied') &&
            (approval.decidedByUser || approval.decidedBy === 'grant' || approval.decidedBy === 'autonomy')
              ? ` by ${deciderLabel(approval.decidedBy, approval.decidedByUser)}`
              : ''}
          </span>
        )}
      </div>
      <p className="mt-1 text-sm break-words whitespace-pre-wrap">{approval.summary}</p>
      {hasApprovalDetail(approval.toolName) && <ApprovalDetail id={approval.id} />}
      {approval.reason && <p className="mt-1 text-xs text-muted">Why: {approval.reason}</p>}
      <p className="mt-1 text-xs text-muted">
        Requested <RelativeTime iso={approval.createdAt} />
        {showThreadLink && approval.threadId && (
          <>
            {' · '}
            <Link className="text-accent underline" to={`/prompts/${approval.threadId}`}>
              Open thread
            </Link>
          </>
        )}
      </p>
      {pending && (
        <div className="mt-2 flex flex-wrap items-center gap-2">
          {confirming && (
            <p role="alert" className="w-full text-sm font-medium">
              This is a critical-risk action. Approve it
              {confirming.remember
                ? ` and stop asking (${rememberLabel(confirming.remember).toLowerCase()})`
                : ''}
              ?
            </p>
          )}
          <Button
            variant={confirming ? 'danger' : 'primary'}
            busy={decide.isPending}
            onClick={() => approve()}
          >
            {confirming ? 'Yes, approve' : 'Approve'}
          </Button>
          {!confirming && grantable && (
            <DropdownMenu.Root modal={false}>
              <DropdownMenu.Trigger
                disabled={decide.isPending}
                className="inline-flex min-h-11 items-center rounded-md border border-border px-3 text-sm hover:bg-surface-2"
                aria-label="Approve and don't ask again"
              >
                Approve and remember ▾
              </DropdownMenu.Trigger>
              <DropdownMenu.Portal>
                <DropdownMenu.Content
                  align="start"
                  className="z-30 min-w-56 rounded-md border border-border bg-surface p-1 shadow-lg"
                >
                  {REMEMBER_OPTIONS.map((option) => (
                    <DropdownMenu.Item
                      key={option.label}
                      className={MENU_ITEM}
                      onSelect={() => approve(option.remember)}
                    >
                      {option.label}
                    </DropdownMenu.Item>
                  ))}
                </DropdownMenu.Content>
              </DropdownMenu.Portal>
            </DropdownMenu.Root>
          )}
          {confirming ? (
            <Button onClick={() => setConfirming(null)}>Cancel</Button>
          ) : (
            <Button
              busy={decide.isPending}
              onClick={() => decide.mutate({ id: approval.id, decision: 'denied' })}
            >
              Deny
            </Button>
          )}
        </div>
      )}
    </section>
  );
}
