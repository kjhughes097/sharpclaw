import { type ReactNode } from 'react';
import { formatAbsolute, formatRelative, useServerNow } from '../../lib/time';

export function Badge({
  children,
  tone = 'neutral',
  title,
}: {
  children: ReactNode;
  tone?: 'neutral' | 'accent' | 'warn' | 'danger' | 'ok';
  title?: string;
}) {
  const tones = {
    neutral: 'bg-surface-2 text-fg',
    accent: 'bg-accent-soft text-fg',
    warn: 'bg-warn-soft text-fg',
    danger: 'bg-danger-soft text-fg',
    ok: 'bg-ok-soft text-fg',
  };
  return (
    <span
      title={title}
      className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${tones[tone]}`}
    >
      {children}
    </span>
  );
}

export function RiskBadge({ risk }: { risk: string | null | undefined }) {
  if (!risk) return null;
  const tone = risk === 'critical' || risk === 'high' ? 'danger' : risk === 'medium' ? 'warn' : 'neutral';
  return <Badge tone={tone}>risk: {risk}</Badge>;
}

/** A relative time with the absolute time available without hover (title + visually hidden text). */
export function RelativeTime({ iso }: { iso: string | null | undefined }) {
  const now = useServerNow();
  if (!iso) return <span>—</span>;
  const absolute = formatAbsolute(iso);
  return (
    <time dateTime={iso} title={absolute}>
      {formatRelative(iso, now)}
      <span className="sr-only"> ({absolute})</span>
    </time>
  );
}
