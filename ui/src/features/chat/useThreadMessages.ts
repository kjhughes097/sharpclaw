import { useQuery } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { api, call } from '../../api/client';
import { keys } from '../../api/queryKeys';
import type { MessageDto } from '../../api/types';

export const PAGE_SIZE = 100;

async function fetchPage(threadId: string, beforeSeq?: number) {
  return call(() =>
    api.GET('/api/threads/{id}/messages', {
      params: {
        path: { id: threadId },
        query: { includeTools: true, limit: PAGE_SIZE, ...(beforeSeq ? { beforeSeq } : {}) },
      },
    }),
  );
}

/**
 * A thread's rows. The latest page lives in the query cache (events invalidate it); older pages
 * loaded with "Load earlier messages" are kept in component state, since history before the latest
 * page doesn't change. Optimistic user messages fill the gap between the 202 and `message.created`.
 */
export function useThreadMessages(threadId: string) {
  const latest = useQuery({
    queryKey: keys.messages(threadId),
    queryFn: () => fetchPage(threadId),
  });
  // `boundary`: the latest page's first seq when older rows were first loaded.
  const [older, setOlder] = useState<{
    threadId: string;
    rows: MessageDto[];
    hasMoreBefore: boolean;
    boundary: number;
  }>({
    threadId,
    rows: [],
    hasMoreBefore: false,
    boundary: 0,
  });
  const [optimistic, setOptimistic] = useState<MessageDto[]>([]);
  const [loadingEarlier, setLoadingEarlier] = useState(false);
  const [earlierError, setEarlierError] = useState<unknown>(null);

  // A new thread starts clean.
  useEffect(() => {
    setOlder({ threadId, rows: [], hasMoreBefore: false, boundary: 0 });
    setOptimistic([]);
    setEarlierError(null);
  }, [threadId]);

  const olderRows = useMemo(() => (older.threadId === threadId ? older.rows : []), [older, threadId]);
  const latestRows = latest.data?.messages;

  // If the latest page has moved past the boundary (more than a page arrived since the older rows
  // were loaded), rows between them are missing: drop the older rows rather than show a gap.
  useEffect(() => {
    if (!latestRows || olderRows.length === 0) return;
    const oldestLatest = latestRows[0]?.seq ?? 0;
    if (latest.data?.hasMoreBefore && oldestLatest > older.boundary)
      setOlder({ threadId, rows: [], hasMoreBefore: false, boundary: 0 });
  }, [latestRows, olderRows, latest.data?.hasMoreBefore, older.boundary, threadId]);

  const rows = useMemo(() => {
    const byId = new Map<string, MessageDto>();
    for (const row of olderRows) byId.set(row.id, row);
    for (const row of latestRows ?? []) byId.set(row.id, row);
    // An optimistic bubble is dropped once the real row (same id) arrives.
    for (const row of optimistic) if (!byId.has(row.id)) byId.set(row.id, row);
    return [...byId.values()].sort((a, b) => (a.seq ?? 0) - (b.seq ?? 0));
  }, [olderRows, latestRows, optimistic]);

  useEffect(() => {
    if (!latestRows || optimistic.length === 0) return;
    const ids = new Set(latestRows.map((r) => r.id));
    if (optimistic.some((o) => ids.has(o.id)))
      setOptimistic((current) => current.filter((o) => !ids.has(o.id)));
  }, [latestRows, optimistic]);

  const hasMoreBefore = olderRows.length > 0 ? older.hasMoreBefore : (latest.data?.hasMoreBefore ?? false);

  const loadEarlier = useCallback(async () => {
    const first = rows.find((r) => typeof r.seq === 'number' && r.seq > 0);
    if (!first?.seq) return;
    setLoadingEarlier(true);
    setEarlierError(null);
    try {
      const page = await fetchPage(threadId, first.seq);
      setOlder((current) => {
        const same = current.threadId === threadId && current.rows.length > 0;
        return {
          threadId,
          rows: [...page.messages, ...(same ? current.rows : [])],
          hasMoreBefore: page.hasMoreBefore ?? false,
          boundary: same ? current.boundary : (latestRows?.[0]?.seq ?? first.seq ?? 0),
        };
      });
    } catch (error) {
      setEarlierError(error);
    } finally {
      setLoadingEarlier(false);
    }
  }, [rows, threadId, latestRows]);

  /** Shows the user's message at once from the 202 response. */
  const addOptimistic = useCallback(
    (message: { id: string; status: string; createdAt: string }, content: string) => {
      const maxSeq = Math.max(0, ...rows.map((r) => r.seq ?? 0));
      setOptimistic((current) => [
        ...current,
        {
          id: message.id,
          role: 'user',
          content,
          status: message.status,
          correlationId: null,
          createdAt: message.createdAt,
          kind: 'message',
          // Sorts after everything loaded until the real row replaces it.
          seq: maxSeq + 0.5 + current.length * 0.01,
          toolCalls: null,
          toolCallId: null,
          toolOk: null,
          untrusted: false,
          truncated: false,
          failReason: null,
        },
      ]);
    },
    [rows],
  );

  return { latest, rows, hasMoreBefore, loadEarlier, loadingEarlier, earlierError, addOptimistic };
}
