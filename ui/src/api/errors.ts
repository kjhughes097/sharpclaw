import { formatClock, serverNow } from '../lib/time';

/** A failed API call, carrying the shared error shape's code and message. */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly retryAfterSeconds: number | null;

  constructor(status: number, code: string, message: string, retryAfterSeconds: number | null = null) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.retryAfterSeconds = retryAfterSeconds;
  }
}

export interface ErrorContext {
  /** Text for `conflict` in this context, e.g. "Already decided." */
  conflict?: string;
}

/** The next UTC midnight (when budgets reset) as a local clock time. */
export function budgetResetLocalTime(now = serverNow()): string {
  const d = new Date(now);
  const reset = Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate() + 1);
  return formatClock(new Date(reset));
}

/**
 * API error codes and message fail reasons as plain text (3.3 spec section 9.11). The server's
 * own message is used only for validation errors, which are written for people.
 */
const MESSAGES: Record<
  string,
  (ctx: { message: string; retryAfter: number | null } & ErrorContext) => string
> = {
  unauthorized: () => 'Your session has ended. Please sign in again.',
  csrf_failed: () => 'Your session needs refreshing. Please try again.',
  rate_limited: ({ retryAfter }) => {
    const minutes = Math.max(1, Math.ceil((retryAfter ?? 60) / 60));
    return `Too many attempts. Try again in ${minutes} ${minutes === 1 ? 'minute' : 'minutes'}.`;
  },
  validation_failed: ({ message }) => message || 'Check the form and try again.',
  conflict: ({ conflict }) => conflict ?? 'Already exists.',
  not_found: () => 'Not found. It may have been removed.',
  agent_disabled: () => 'This agent is disabled.',
  budget_exceeded: () => `Daily budget used up. Resets at ${budgetResetLocalTime()}.`,
  context_too_large: () =>
    "That is too large for the model's context window. Shorten it or start a new chat.",
  provider_error: () => 'The model provider returned an error. Try again.',
  provider_timeout: () => 'The model provider timed out. Try again.',
  agent_error: () => 'The agent hit an internal error.',
  no_approver: () => 'An approval was needed but nobody was available.',
  approval_expired: () => 'An approval request expired before it was answered.',
  denied_by_user: () => 'You denied a tool request.',
  internal_error: () => 'Something went wrong. Try again.',
};

/** Plain text for an API error code. */
export function messageForCode(
  code: string,
  extra: { message?: string; retryAfter?: number | null } & ErrorContext = {},
): string {
  const fn = MESSAGES[code];
  return fn
    ? fn({ message: extra.message ?? '', retryAfter: extra.retryAfter ?? null, conflict: extra.conflict })
    : 'Something went wrong. Try again.';
}

/** Plain text for any thrown error. */
export function errorMessage(error: unknown, ctx: ErrorContext = {}): string {
  if (error instanceof ApiError)
    return messageForCode(error.code, {
      message: error.message,
      retryAfter: error.retryAfterSeconds,
      ...ctx,
    });
  return 'Something went wrong. Try again.';
}

/** Plain text for a failed user message's `failReason`. */
export function failReasonMessage(code: string | null | undefined): string {
  if (!code) return 'The agent could not complete this turn.';
  if (code === 'internal_error' || !(code in MESSAGES))
    return `The agent could not complete this turn (${code}).`;
  return messageForCode(code);
}
