import { serverNow } from '../../lib/time';

export type AuditTab = 'llm' | 'tools';

export const LLM_STATUSES = ['ok', 'error', 'cancelled', 'rejected'] as const;
export const TOOL_OUTCOMES = [
  'requested',
  'succeeded',
  'failed',
  'denied',
  'blocked',
  'expired',
  'unknown_tool',
  'invalid_arguments',
] as const;

/** Time-window filter values (URL `window` param) and their length. */
export const TIME_WINDOWS = [
  { value: '1h', label: '1 hour', ms: 3_600_000 },
  { value: '24h', label: '24 hours', ms: 86_400_000 },
  { value: '7d', label: '7 days', ms: 7 * 86_400_000 },
] as const;

/** Tool calls not reported this long after they were requested get the "unreported" marker. */
export const UNREPORTED_AFTER_MS = 5 * 60_000;

/** The first 8 characters of an id: enough to tell rows apart at a glance. */
export function shortId(id: string): string {
  return id.slice(0, 8);
}

/** The `since` lower bound for a time window, on the server's clock; undefined for "All". */
export function sinceFor(window: string | undefined, now = serverNow()): string | undefined {
  const match = TIME_WINDOWS.find((w) => w.value === window);
  return match ? new Date(now - match.ms).toISOString() : undefined;
}

export function isUnreported(
  call: { reportedAt: string | null; createdAt: string },
  now = serverNow(),
): boolean {
  if (call.reportedAt) return false;
  const created = Date.parse(call.createdAt);
  return !Number.isNaN(created) && now - created > UNREPORTED_AFTER_MS;
}

export type Tone = 'neutral' | 'accent' | 'warn' | 'danger' | 'ok';

/** Badge tone for an LLM call status, tool outcome or turn status. */
export function statusTone(status: string | null | undefined): Tone {
  switch (status) {
    case 'ok':
    case 'succeeded':
    case 'done':
    case 'approved':
      return 'ok';
    case 'error':
    case 'failed':
    case 'unknown_tool':
    case 'invalid_arguments':
      return 'danger';
    case 'rejected':
    case 'denied':
    case 'blocked':
    case 'expired':
      return 'warn';
    case 'requested':
    case 'pending':
    case 'processing':
      return 'accent';
    default:
      return 'neutral';
  }
}

/** Snake-case codes shown as words ("unknown_tool" → "unknown tool"). */
export function humanize(value: string | null | undefined): string {
  return value ? value.replaceAll('_', ' ') : '—';
}
