import { useQuery } from '@tanstack/react-query';
import { Tabs } from 'radix-ui';
import { useSearchParams } from 'react-router';
import { api, call } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { usePendingApprovals } from '../../api/hooks';
import { keys } from '../../api/queryKeys';
import type { ApprovalDetailDto } from '../../api/types';
import { EmptyState, ErrorState, LoadingState } from '../../components/ui';
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
    <div className="flex min-h-0 flex-1 flex-col overflow-y-auto">
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
          <ApprovalList query={pending} empty="Nothing is waiting for you." />
        </Tabs.Content>
        <Tabs.Content value="recent">
          <ApprovalList query={recent} empty="No approvals yet." />
        </Tabs.Content>
      </Tabs.Root>
    </div>
  );
}
