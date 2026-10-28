import { useState } from 'react';
import type { UsageBucketDto, UsageByModelDto } from '../../api/types';
import { formatBucket, formatCount, formatMoney } from '../../lib/time';

const VIEW_HEIGHT = 100;
const BAR_WIDTH = 10;
const GAP = 2;

/**
 * Stacked token bars: input (blue) at the bottom, output (amber) on top, scaled to the busiest
 * bucket. Plain SVG with sizes in attributes (never inline style attributes, for the CSP). Mouse
 * hover shows a tooltip; keyboard and screen-reader users get the same figures in a hidden table.
 */
export function TokenBars({
  buckets,
  bucketSeconds,
  summary,
}: {
  buckets: UsageBucketDto[];
  bucketSeconds: number;
  /** The chart's text alternative, e.g. "Last 7 days: 4,092 tokens, $0.01". */
  summary: string;
}) {
  const [hover, setHover] = useState<number | null>(null);
  const max = Math.max(1, ...buckets.map((b) => b.promptTokens + b.completionTokens));
  const width = Math.max(1, buckets.length) * (BAR_WIDTH + GAP);
  const shown = hover === null ? undefined : buckets[hover];
  const first = buckets[0];
  const last = buckets[buckets.length - 1];

  return (
    <div className="relative flex min-h-0 flex-1 flex-col">
      <svg
        role="img"
        aria-label={summary}
        viewBox={`0 0 ${width} ${VIEW_HEIGHT}`}
        preserveAspectRatio="none"
        className="h-28 w-full"
        onMouseLeave={() => setHover(null)}
      >
        {buckets.map((b, i) => {
          const inH = (b.promptTokens / max) * VIEW_HEIGHT;
          const outH = (b.completionTokens / max) * VIEW_HEIGHT;
          const x = i * (BAR_WIDTH + GAP);
          return (
            <g key={b.start} onMouseEnter={() => setHover(i)} data-testid="usage-bar">
              {/* A full-height hit area so thin bars are easy to hover. */}
              <rect x={x} y={0} width={BAR_WIDTH} height={VIEW_HEIGHT} className="fill-transparent" />
              <rect x={x} y={VIEW_HEIGHT - inH} width={BAR_WIDTH} height={inH} className="fill-chart-in" />
              <rect
                x={x}
                y={VIEW_HEIGHT - inH - outH}
                width={BAR_WIDTH}
                height={outH}
                className="fill-chart-out"
              />
            </g>
          );
        })}
      </svg>
      {shown && (
        <div
          role="tooltip"
          className="pointer-events-none absolute top-0 right-0 rounded border border-border bg-surface-2 px-2 py-1 font-mono text-xs"
        >
          <div>{formatBucket(shown.start, bucketSeconds)}</div>
          <div>
            in {formatCount(shown.promptTokens)} · out {formatCount(shown.completionTokens)}
          </div>
          <div>
            {formatCount(shown.calls)} calls · {formatMoney(shown.costUsd)}
          </div>
        </div>
      )}
      {first && last && (
        <div className="mt-1 flex justify-between font-mono text-xs text-muted" aria-hidden="true">
          <span>{formatBucket(first.start, bucketSeconds)}</span>
          <span>{formatBucket(last.start, bucketSeconds)}</span>
        </div>
      )}
      <div className="mt-1 flex gap-3 text-xs text-muted" aria-hidden="true">
        <span className="inline-flex items-center gap-1">
          <span className="h-2 w-2 bg-chart-in" /> in
        </span>
        <span className="inline-flex items-center gap-1">
          <span className="h-2 w-2 bg-chart-out" /> out
        </span>
      </div>
      <table className="sr-only">
        <caption>{summary}</caption>
        <thead>
          <tr>
            <th scope="col">From</th>
            <th scope="col">Input tokens</th>
            <th scope="col">Output tokens</th>
            <th scope="col">Calls</th>
            <th scope="col">Cost</th>
          </tr>
        </thead>
        <tbody>
          {buckets.map((b) => (
            <tr key={b.start}>
              <th scope="row">{formatBucket(b.start, bucketSeconds)}</th>
              <td>{b.promptTokens}</td>
              <td>{b.completionTokens}</td>
              <td>{b.calls}</td>
              <td>{formatMoney(b.costUsd)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** How many models get their own bar; the rest are summed into "other". */
export const MAX_MODEL_BARS = 6;

export function topModels(models: UsageByModelDto[]): { model: string; costUsd: number }[] {
  const top = models.slice(0, MAX_MODEL_BARS).map((m) => ({ model: m.model, costUsd: m.costUsd }));
  const rest = models.slice(MAX_MODEL_BARS);
  if (rest.length > 0) top.push({ model: 'other', costUsd: rest.reduce((sum, m) => sum + m.costUsd, 0) });
  return top;
}

/** Horizontal cost bars scaled to the most expensive model; widths via the style prop (CSSOM, CSP-safe). */
export function ModelCostBars({ models }: { models: UsageByModelDto[] }) {
  const rows = topModels(models);
  const max = Math.max(...rows.map((r) => r.costUsd), 0);
  return (
    <ul className="space-y-1.5" aria-label="Cost by model">
      {rows.map((row) => (
        <li
          key={row.model}
          className="grid grid-cols-[minmax(0,7rem)_1fr_auto] items-center gap-2 font-mono text-xs"
        >
          <span className="truncate" title={row.model}>
            {row.model}
          </span>
          <span className="h-1.5 rounded-full bg-surface-2" aria-hidden="true">
            <span
              className="block h-1.5 rounded-full bg-accent"
              style={{ width: `${max > 0 ? Math.max(2, (row.costUsd / max) * 100) : 0}%` }}
            />
          </span>
          <span>{formatMoney(row.costUsd)}</span>
        </li>
      ))}
    </ul>
  );
}
