import { useEffect, useState } from 'react';
import { api, call } from '../../api/client';
import type { ThreadDetailResponse } from '../../api/types';
import type { ApprovalView } from '../approvals/ApprovalCard';

export type TurnApproval = ApprovalView & { correlationId: string };

/** Keeps decided approvals of the in-flight turn on screen (muted) after they leave the pending list. */
export function useTurnApprovals(
  threadId: string,
  pending: ThreadDetailResponse['pendingApprovals'] | undefined,
): Map<string, TurnApproval> {
  const [known, setKnown] = useState<Map<string, TurnApproval>>(new Map());
  useEffect(() => setKnown(new Map()), [threadId]);

  useEffect(() => {
    if (!pending) return;
    setKnown((current) => {
      const next = new Map(current);
      const ids = new Set(pending.map((p) => p.id));
      for (const p of pending) next.set(p.id, { ...p, status: 'pending', threadId });
      for (const [id, view] of next)
        if (!ids.has(id) && view.status === 'pending') next.set(id, { ...view, status: 'decided' });
      return next;
    });
  }, [pending, threadId]);

  // Fetch the outcome of approvals that left the pending list (approved, denied or expired).
  const undecided = [...known.values()].filter((v) => v.status === 'decided').map((v) => v.id);
  const undecidedKey = undecided.join(',');
  useEffect(() => {
    for (const id of undecidedKey.split(',').filter(Boolean))
      call(() => api.GET('/api/approvals/{id}', { params: { path: { id } } }))
        .then(({ approval }) =>
          setKnown((current) =>
            new Map(current).set(id, {
              ...current.get(id)!,
              status: approval.status,
              decidedBy: approval.decidedBy,
              decidedByUser: approval.decidedByUser,
            }),
          ),
        )
        .catch(() =>
          setKnown((current) => new Map(current).set(id, { ...current.get(id)!, status: 'expired' })),
        );
  }, [undecidedKey]);

  return known;
}
