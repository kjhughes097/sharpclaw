import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Tabs } from 'radix-ui';
import { useState } from 'react';
import { useSearchParams } from 'react-router';
import { api, call } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { usePendingApprovals } from '../../api/hooks';
import { keys } from '../../api/queryKeys';
import type { ApprovalDetailDto } from '../../api/types';
import { useToast } from '../../app/toast';
import { Button, ConfirmDialog, EmptyState, ErrorState, LoadingState } from '../../components/ui';
import { ApprovalCard } from './ApprovalCard';

function ApprovalList({
  query,
  empty,
}: {
  query: { data?: ApprovalDetailDto[]; isPending: boolean; error: unknown; refetch: () => unknown };
  empty: string;
}) {
  if (query.isPending) return <LoadingState label="Loading approvals" />;
  if (query.error)
    return <ErrorState message={errorMessage(query.error)} onRetry={() => void query.refetch()} />;
  if (!query.data?.length)
    return (
      <EmptyState title={empty}>
        Approvals appear here when an agent needs your permission to use a tool.
      </EmptyState>
    );
  return (
    <ul className="space-y-3 p-4">
      {query.data.map((approval) => (
        <li key={approval.id}>
          <ApprovalCard approval={approval} showThreadLink />
        </li>
      ))}
    </ul>
  );
}

/** Approves every pending approval shown, in one request (3.6 spec Q9), after a confirmation. */
function ApproveAll({ approvals }: { approvals: ApprovalDetailDto[] }) {
  const queryClient = useQueryClient();
  const toast = useToast();
  const [open, setOpen] = useState(false);
  const batch = useMutation({
    mutationFn: (ids: string[]) =>
      call(() => api.POST('/api/approvals/batch', { body: { decision: 'approved', ids } })),
    onSuccess: (result) =>
      toast(
        `Approved ${result.decided.length}.${result.skipped ? ` ${result.skipped} were already decided.` : ''}`,
        'success',
      ),
    onError: (err) => toast(errorMessage(err), 'error'),
    onSettled: () => {
      setOpen(false);
      void queryClient.invalidateQueries({ queryKey: keys.approvals });
      void queryClient.invalidateQueries({ queryKey: keys.allThreadDetails });
    },
  });
  if (approvals.length < 2) return null;
  const critical = approvals.some((a) => a.risk === 'critical');
  return (
    <>
      <Button onClick={() => setOpen(true)}>Approve all ({approvals.length})</Button>
      <ConfirmDialog
        open={open}
        onOpenChange={setOpen}
        title={`Approve all ${approvals.length} pending approvals?`}
        description={
          critical
            ? 'At least one of them is critical risk. Each call runs once; nothing is remembered.'
            : 'Each call runs once; nothing is remembered.'
        }
        confirmLabel="Approve all"
        danger={critical}
        busy={batch.isPending}
        onConfirm={() => batch.mutate(approvals.slice(0, 100).map((a) => a.id))}
      />
    </>
  );
}

/** Pending (default) and Recent tabs (3.3 spec section 9.4). */
export function ApprovalsPage() {
  const [params, setParams] = useSearchParams();
  const tab = params.get('tab') === 'recent' ? 'recent' : 'pending';
  const pending = usePendingApprovals();
  const recent = useQuery({
    queryKey: keys.approvalList('recent'),
    queryFn: () => call(() => api.GET('/api/approvals', { params: { query: { limit: 50 } } })),
    select: (data) => data.approvals,
    enabled: tab === 'recent',
  });

  return (
    <div className="relative flex min-h-0 flex-1 flex-col overflow-y-auto">
      <h1 className="px-4 pt-4 text-xl font-semibold">Approvals</h1>
      <Tabs.Root
        value={tab}
        onValueChange={(value) => setParams(value === 'recent' ? { tab: 'recent' } : {})}
      >
        <Tabs.List aria-label="Approvals" className="mt-2 flex gap-1 border-b border-border px-4">
          {(['pending', 'recent'] as const).map((value) => (
            <Tabs.Trigger
              key={value}
              value={value}
              className="min-h-11 border-b-2 border-transparent px-3 text-sm font-medium data-[state=active]:border-accent"
            >
              {value === 'pending'
                ? `Pending${pending.data?.length ? ` (${pending.data.length})` : ''}`
                : 'Recent'}
            </Tabs.Trigger>
          ))}
        </Tabs.List>
        <Tabs.Content value="pending">
          {pending.data && pending.data.length > 1 && (
            <div className="flex justify-end px-4 pt-3">
              <ApproveAll approvals={pending.data} />
            </div>
          )}
          <ApprovalList query={pending} empty="Nothing is waiting for you." />
        </Tabs.Content>
        <Tabs.Content value="recent">
          <ApprovalList query={recent} empty="No approvals yet." />
        </Tabs.Content>
      </Tabs.Root>
    </div>
  );
}
