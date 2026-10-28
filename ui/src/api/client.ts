import createClient, { type Middleware } from 'openapi-fetch';
import { ApiError } from './errors';
import type { paths } from './schema';

/**
 * The typed client for the gateway's client API (same origin, session cookie). The CSRF token
 * lives only in this module's memory: never in storage, URLs or logs (3.3 spec section 9.1).
 */
let csrfToken: string | null = null;

export function setCsrfToken(token: string | null) {
  csrfToken = token;
}

export function hasCsrfToken() {
  return csrfToken !== null;
}

const csrfMiddleware: Middleware = {
  onRequest({ request }) {
    if (request.method !== 'GET' && request.method !== 'HEAD' && csrfToken)
      request.headers.set('X-CSRF-Token', csrfToken);
    return request;
  },
};

// Absolute same-origin URLs (Node's fetch, used by the tests, rejects relative ones).
const origin = typeof location !== 'undefined' ? location.origin : '';

export const api = createClient<paths>({
  baseUrl: origin,
  credentials: 'same-origin',
  // Resolved per call rather than captured at import, so test interceptors see every request.
  fetch: (request) => globalThis.fetch(request),
});
api.use(csrfMiddleware);

// ----- Signed-out handling -----

type Handler = () => void;
let onUnauthorized: Handler | null = null;
let unauthorizedPending = false;

/** Registered by the auth provider: clears caches, closes the stream, goes to the login page. */
export function setUnauthorizedHandler(handler: Handler | null) {
  onUnauthorized = handler;
  unauthorizedPending = false;
}

/** Several failing calls cause one redirect. */
export function notifyUnauthorized() {
  if (unauthorizedPending) return;
  unauthorizedPending = true;
  queueMicrotask(() => onUnauthorized?.());
}

/** Re-checks the session (used after `csrf_failed` and by the stream manager). */
export async function refreshSession(): Promise<boolean> {
  try {
    const res = await fetch(`${origin}/api/auth/session`, { credentials: 'same-origin', headers: { Accept: 'application/json' } });
    if (!res.ok) return false;
    const body = (await res.json()) as { authenticated: boolean; csrfToken?: string };
    if (body.authenticated && body.csrfToken) setCsrfToken(body.csrfToken);
    return body.authenticated;
  } catch {
    // Network trouble is not a sign-out; the caller retries later.
    return true;
  }
}

type Result<T> = { data?: T; error?: unknown; response: Response };

function toApiError(result: { error?: unknown; response: Response }): ApiError {
  const body = result.error as { error?: { code?: string; message?: string } } | undefined;
  const retry = Number(result.response.headers.get('Retry-After'));
  return new ApiError(
    result.response.status,
    body?.error?.code ?? (result.response.status === 401 ? 'unauthorized' : 'internal_error'),
    body?.error?.message ?? result.response.statusText,
    Number.isFinite(retry) && retry > 0 ? retry : null,
  );
}

/**
 * Runs an API call and returns its data or throws an {@link ApiError}. A 401 signs the user out; a
 * `csrf_failed` re-fetches the session and retries once if still signed in.
 */
export async function call<T>(fn: () => Promise<Result<T>>, options: { signOutOn401?: boolean } = {}): Promise<T> {
  let result = await fn();
  if (!result.response.ok) {
    const error = toApiError(result);
    if (error.status === 403 && error.code === 'csrf_failed') {
      if (await refreshSession()) result = await fn();
      else {
        notifyUnauthorized();
        throw error;
      }
    }
  }
  if (!result.response.ok) {
    const error = toApiError(result);
    if (error.status === 401 && options.signOutOn401 !== false) notifyUnauthorized();
    throw error;
  }
  return result.data as T;
}
