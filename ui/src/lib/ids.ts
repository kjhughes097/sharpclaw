let counter = 0;

/**
 * A client-side id for keys and optimistic rows. Not cryptographic: `crypto.randomUUID()` needs a
 * secure context, and the server is the source of truth for real ids.
 */
export function localId(prefix = 'l'): string {
  counter = (counter + 1) % Number.MAX_SAFE_INTEGER;
  return `${prefix}-${Date.now().toString(36)}-${counter.toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}
