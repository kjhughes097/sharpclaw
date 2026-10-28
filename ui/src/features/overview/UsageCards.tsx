import { useId } from 'react';
import { errorMessage } from '../../api/errors';
import { useCredits, useUsage } from '../../api/hooks';
import type { UsageRange, UsageResponse } from '../../api/types';
import { ErrorState, Spinner } from '../../components/ui';
import { formatCount, formatMoney } from '../../lib/time';
import { Card } from './Card';
import { ModelCostBars, TokenBars } from './charts';

export const RANGES: Record<UsageRange, { title: string; caption: string; total: string }> = {
  today: { title: 'Today', caption: 'tokens per hour', total: 'Total cost today' },
  '7d': { title: 'Last 7 days', caption: 'tokens per 6 hours', total: 'Total cost, 7 days' },
  '30d': { title: 'Last 30 days', caption: 'tokens per day', total: 'Total cost, 30 days' },
};

export function totalTokens(u: Pick<UsageResponse['totals'], 'promptTokens' | 'completionTokens'>): number {
  return u.promptTokens + u.completionTokens;
}

type UsageQuery = ReturnType<typeof useUsage>;

/** One of the Today / 7 days / 30 days cards: stacked token bars and the total cost. */
export function UsageCard({ range, usage }: { range: UsageRange; usage: UsageQuery }) {
  const meta = RANGES[range];
  const titleId = useId();
  const data = usage.data;
  return (
    <Card
      title={meta.title}
      titleId={titleId}
      caption={meta.caption}
      footer={
        <>
          <span>{meta.total}</span>
          <span className="font-mono font-semibold text-accent">
            {data ? formatMoney(data.totals.costUsd) : '—'}
          </span>
        </>
      }
    >
      {usage.isPending ? (
        <Spinner label={`Loading ${meta.title.toLowerCase()}`} />
      ) : usage.isError ? (
        <ErrorState message={errorMessage(usage.error)} onRetry={() => void usage.refetch()} />
      ) : (
        <TokenBars
          buckets={data!.buckets}
          bucketSeconds={data!.bucketSeconds}
          summary={`${meta.title}: ${formatCount(totalTokens(data!.totals))} tokens, ${formatMoney(data!.totals.costUsd)}`}
        />
      )}
    </Card>
  );
}

export function CostByModelCard({ usage }: { usage: UsageQuery }) {
  const titleId = useId();
  return (
    <Card title="Cost by model" titleId={titleId} caption="last 7 days">
      {usage.isPending ? (
        <Spinner label="Loading cost by model" />
      ) : usage.isError ? (
        <ErrorState message={errorMessage(usage.error)} onRetry={() => void usage.refetch()} />
      ) : usage.data.byModel.length === 0 ? (
        <p className="text-sm text-muted">No model calls in the last 7 days.</p>
      ) : (
        <ModelCostBars models={usage.data.byModel} />
      )}
    </Card>
  );
}

/** The cached OpenRouter balance and how fresh it is (3.3.1 spec section 5.2). */
export function CreditsCard() {
  const credits = useCredits();
  const titleId = useId();
  const data = credits.data;
  const low = data?.remainingUsd != null && data.remainingUsd < data.lowThresholdUsd;

  let body;
  if (credits.isPending) body = <Spinner label="Loading credits" />;
  else if (credits.isError)
    body = <ErrorState message={errorMessage(credits.error)} onRetry={() => void credits.refetch()} />;
  else if (data!.status === 'not_configured')
    body = (
      <p className="text-sm text-muted">
        Set <code>OpenRouter__ManagementKey</code> to show credits.
      </p>
    );
  else if (data!.status === 'unsupported')
    body = <p className="text-sm text-muted">Not available with the fake provider.</p>;
  else
    body = (
      <p
        className={`m-auto py-4 font-mono text-4xl font-semibold ${low ? 'text-danger' : ''}`}
        data-testid="credits-amount"
      >
        {data!.remainingUsd == null ? '—' : `$${data!.remainingUsd.toFixed(2)}`}
        {low && <span className="sr-only"> (low)</span>}
      </p>
    );

  const showFooter = data && (data.status === 'ok' || data.status === 'stale' || data.status === 'failing');
  return (
    <Card
      title="Credits Remaining"
      titleId={titleId}
      caption={data?.status === 'stale' ? 'stale' : undefined}
      footer={
        showFooter ? (
          <>
            <span>{data.asOf ? `as of ${new Date(data.asOf).toLocaleDateString()}` : 'never fetched'}</span>
            {data.status === 'failing' && <span className="text-danger">daily fetch failing</span>}
          </>
        ) : undefined
      }
    >
      {body}
    </Card>
  );
}
