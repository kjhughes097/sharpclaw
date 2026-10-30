import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link } from 'react-router';
import { api, call } from '../../api/client';
import { ApiError, errorMessage } from '../../api/errors';
import { keys } from '../../api/queryKeys';
import { HIGHEST_RISK } from '../../api/types';
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

/** Decides an approval; a 409 (already decided or expired) shows a notice and refreshes. */
export function useDecideApproval(threadId?: string) {
  const queryClient = useQueryClient();
  const toast = useToast();
  return useMutation({
    mutationFn: ({ id, decision }: { id: string; decision: 'approved' | 'denied' }) =>
      call(() => api.POST('/api/approvals/{id}', { params: { path: { id } }, body: { decision } })),
    onError: (error) => {
      const conflict = error instanceof ApiError && error.code === 'conflict';
      toast(conflict ? 'Already decided.' : errorMessage(error), conflict ? 'info' : 'error');
    },
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: keys.approvals });
      if (threadId) void queryClient.invalidateQueries({ queryKey: keys.thread(threadId) });
    },
  });
}

const STATUS_TEXT: Record<string, string> = {
  approved: 'Approved',
  denied: 'Denied',
  expired: 'Expired — the agent was told',
};

/**
 * One approval: tool, risk, summary, reason, countdown and Approve / Deny. The highest risk
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
  const [confirming, setConfirming] = useState(false);
  const due = Date.parse(approval.expiresAt) <= now;
  const pending = approval.status === 'pending' && !due;
  const critical = approval.risk === HIGHEST_RISK;
  const status = approval.status === 'pending' && due ? 'expired' : approval.status;

  const approve = () => {
    if (critical && !confirming) {
      setConfirming(true);
      return;
    }
    decide.mutate({ id: approval.id, decision: 'approved' });
    setConfirming(false);
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
            {(status === 'approved' || status === 'denied') && approval.decidedByUser
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
              This is a critical-risk action. Approve it?
            </p>
          )}
          <Button variant={confirming ? 'danger' : 'primary'} busy={decide.isPending} onClick={approve}>
            {confirming ? 'Yes, approve' : 'Approve'}
          </Button>
          {confirming ? (
            <Button onClick={() => setConfirming(false)}>Cancel</Button>
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
