import { useQuery } from '@tanstack/react-query';
import { api, call } from '../../api/client';
import { keys } from '../../api/queryKeys';

/**
 * Broker tools whose approval needs more than the content-free summary to judge (external tools
 * spec Q1): the command for sandbox_run, the text of a GitHub write, a PR's file list.
 */
const TOOLS_WITH_DETAIL = new Set(['sandbox_run', 'github_write', 'github_create_pr']);

export const hasApprovalDetail = (toolName: string) => TOOLS_WITH_DETAIL.has(toolName);

/**
 * The gateway-rendered detail of one approval, from `GET /api/approvals/{id}` (lists and events
 * never carry it). Shown as plain text, never interpreted.
 */
export function ApprovalDetail({ id }: { id: string }) {
  const detail = useQuery({
    queryKey: keys.approval(id),
    queryFn: () => call(() => api.GET('/api/approvals/{id}', { params: { path: { id } } })),
    select: (data) => data.approval.detail ?? null,
    // The detail never changes once the approval exists.
    staleTime: Infinity,
  });

  if (detail.isPending) return <p className="mt-1 text-xs text-muted">Loading details…</p>;
  if (detail.isError) return <p className="mt-1 text-xs text-muted">Details unavailable.</p>;
  if (!detail.data) return null;
  return (
    <pre
      aria-label="Approval details"
      className="mt-2 max-h-64 overflow-auto rounded border border-border bg-surface p-2 font-mono text-xs break-words whitespace-pre-wrap"
    >
      {detail.data}
    </pre>
  );
}
