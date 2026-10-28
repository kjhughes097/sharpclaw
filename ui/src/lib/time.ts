import { useEffect, useState, useSyncExternalStore } from 'react';

/** Locale for dates and numbers: the browser's, defaulting to en-GB. */
export function locale(): string {
  return (typeof navigator !== 'undefined' && navigator.language) || 'en-GB';
}

// ----- Server clock -----

let serverOffsetMs = 0;
const offsetListeners = new Set<() => void>();

function notifyOffset() {
  offsetListeners.forEach((listener) => listener());
}

/** Records the server clock offset from `GET /api/system`'s `utcNow`, used for countdowns. */
export function setServerTime(utcNow: string, receivedAt = Date.now()) {
  const server = Date.parse(utcNow);
  if (Number.isNaN(server)) return;
  serverOffsetMs = server - receivedAt;
  notifyOffset();
}

/** Milliseconds since the epoch on the server's clock. */
export function serverNow(): number {
  return Date.now() + serverOffsetMs;
}

export function resetServerTime() {
  serverOffsetMs = 0;
  notifyOffset();
}

function subscribeOffset(listener: () => void) {
  offsetListeners.add(listener);
  return () => {
    offsetListeners.delete(listener);
  };
}

/**
 * The server's "now" for rendering. Re-renders when the clock offset is learned or changes (the
 * first `/api/system` response may arrive after the component's first render) and every
 * `tickMs`, so relative times and staleness never stay computed from the browser's clock.
 */
export function useServerNow(tickMs = 30_000): number {
  useSyncExternalStore(subscribeOffset, () => serverOffsetMs);
  const [, tick] = useState(0);
  useEffect(() => {
    const timer = setInterval(() => tick((n) => n + 1), tickMs);
    return () => clearInterval(timer);
  }, [tickMs]);
  return serverNow();
}

// ----- Formatting -----

export function formatAbsolute(iso: string | null | undefined): string {
  if (!iso) return '';
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  return new Intl.DateTimeFormat(locale(), { dateStyle: 'medium', timeStyle: 'medium' }).format(date);
}

export function formatClock(iso: string | Date): string {
  const date = typeof iso === 'string' ? new Date(iso) : iso;
  return new Intl.DateTimeFormat(locale(), { hour: '2-digit', minute: '2-digit' }).format(date);
}

/** "just now", "5 min ago", "3 h ago", "2 d ago", else a date. */
export function formatRelative(iso: string | null | undefined, now = serverNow()): string {
  if (!iso) return '';
  const then = Date.parse(iso);
  if (Number.isNaN(then)) return '';
  const seconds = Math.round((now - then) / 1000);
  if (seconds < 45) return 'just now';
  const rtf = new Intl.RelativeTimeFormat(locale(), { numeric: 'auto', style: 'short' });
  if (seconds < 3600) return rtf.format(-Math.round(seconds / 60), 'minute');
  if (seconds < 86400) return rtf.format(-Math.round(seconds / 3600), 'hour');
  if (seconds < 86400 * 7) return rtf.format(-Math.round(seconds / 86400), 'day');
  return new Intl.DateTimeFormat(locale(), { dateStyle: 'medium' }).format(new Date(then));
}

/** "4:05" style countdown to `iso` on the server clock; "0:00" once due. */
export function formatCountdown(iso: string, now = serverNow()): string {
  const ms = Math.max(0, Date.parse(iso) - now);
  const total = Math.ceil(ms / 1000);
  const minutes = Math.floor(total / 60);
  const seconds = total % 60;
  return `${minutes}:${seconds.toString().padStart(2, '0')}`;
}

export function formatDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined) return '—';
  if (ms < 1000) return `${Math.round(ms)} ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)} s`;
  return `${Math.floor(ms / 60_000)} min ${Math.round((ms % 60_000) / 1000)} s`;
}

/** Costs: "$0.0042"; anything below $0.0001 (but not zero) is "<$0.0001". */
export function formatCost(usd: number | null | undefined): string {
  if (usd === null || usd === undefined) return '—';
  if (usd > 0 && usd < 0.0001) return '<$0.0001';
  return `$${usd.toFixed(4)}`;
}

/** Budgets and spend in USD via Intl. */
export function formatUsd(usd: number): string {
  return new Intl.NumberFormat(locale(), {
    style: 'currency',
    currency: 'USD',
    maximumFractionDigits: 2,
  }).format(usd);
}

export function formatTokens(n: number): string {
  if (n < 1000) return `${n}`;
  return `${(n / 1000).toFixed(n < 10_000 ? 1 : 0)}k`;
}

/** Dashboard money (3.3.1): "$0.00", with anything above zero but under a cent shown as "<$0.01". */
export function formatMoney(usd: number | null | undefined): string {
  if (usd === null || usd === undefined) return '—';
  if (usd > 0 && usd < 0.01) return '<$0.01';
  return `$${usd.toFixed(2)}`;
}

/** Whole numbers with thousands separators ("4,092"). */
export function formatCount(n: number): string {
  return new Intl.NumberFormat(locale()).format(n);
}

/** A chart bucket in local time: "27 Sep" for daily buckets, "26 Oct 13:00" otherwise. */
export function formatBucket(iso: string, bucketSeconds: number): string {
  const date = new Date(iso);
  const day = new Intl.DateTimeFormat(locale(), { day: 'numeric', month: 'short' }).format(date);
  return bucketSeconds >= 86_400 ? day : `${day} ${formatClock(date)}`;
}

/** "16:30:41": a message's local time of day. */
export function formatTimeOfDay(iso: string): string {
  return new Intl.DateTimeFormat(locale(), {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  }).format(new Date(iso));
}
