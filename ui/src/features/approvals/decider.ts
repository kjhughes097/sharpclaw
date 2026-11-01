/**
 * Who decided an approval, for display: a user name, "admin key" (the CLI), a standing approval or
 * autonomy mode (3.6 spec Q10), or the system.
 */
export function deciderLabel(
  decidedBy: string | null | undefined,
  decidedByUser: string | null | undefined,
): string | null {
  if (decidedByUser) return decidedByUser === 'admin_key' ? 'admin key' : decidedByUser;
  if (decidedBy === 'grant') return 'standing approval';
  if (decidedBy === 'autonomy') return 'autonomy mode';
  return decidedBy ?? null;
}
