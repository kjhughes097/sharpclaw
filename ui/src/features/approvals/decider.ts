/** Who decided an approval, for display: a user name, "admin key" (the CLI), or the system. */
export function deciderLabel(
  decidedBy: string | null | undefined,
  decidedByUser: string | null | undefined,
): string | null {
  if (decidedByUser) return decidedByUser === 'admin_key' ? 'admin key' : decidedByUser;
  return decidedBy ?? null;
}
