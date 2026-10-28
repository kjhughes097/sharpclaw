import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import type { components } from '../src/api/schema';

type S = components['schemas'];

/** Builders for API payloads with sensible defaults. */
export const NOW = '2026-10-25T12:00:00Z';

export type Range = 'today' | '7d' | '30d';
const BUCKETS: Record<Range, { count: number; seconds: number; from: string }> = {
  today: { count: 13, seconds: 3600, from: '2026-10-25T00:00:00Z' },
  '7d': { count: 28, seconds: 21600, from: '2026-10-18T18:00:00Z' },
  '30d': { count: 30, seconds: 86400, from: '2026-09-26T00:00:00Z' },
};

export const fixtures = {
  /** A zero-filled usage response for `range`; pass `over` to add usage. */
  usage: (range: Range, over: Partial<S['UsageResponse']> = {}): S['UsageResponse'] => {
    const b = BUCKETS[range];
    const from = Date.parse(b.from);
    return {
      range,
      from: b.from,
      to: NOW,
      bucketSeconds: b.seconds,
      buckets: Array.from({ length: b.count }, (_, i) => ({
        start: new Date(from + i * b.seconds * 1000).toISOString(),
        calls: 0,
        promptTokens: 0,
        completionTokens: 0,
        costUsd: 0,
      })),
      byModel: [],
      byAgent: [
        {
          agentId: 'agent-1',
          agentName: 'assistant',
          calls: 0,
          promptTokens: 0,
          completionTokens: 0,
          costUsd: 0,
        },
      ],
      totals: { calls: 0, promptTokens: 0, completionTokens: 0, costUsd: 0, unknownUsageCalls: 0 },
      ...over,
    };
  },
  agent: (over: Partial<S['AgentDto']> = {}): S['AgentDto'] => ({
    id: 'agent-1',
    name: 'assistant',
    model: 'openai/gpt-test',
    dailyBudgetUsd: 1,
    enabled: true,
    createdAt: '2026-10-25T10:00:00Z',
    todaySpendUsd: 0.25,
    remainingUsd: 0.75,
    contextWindowTokens: null,
    lastSeenAt: '2026-10-25T11:59:30Z',
    ...over,
  }),
  thread: (over: Partial<S['ThreadSummaryDto']> = {}): S['ThreadSummaryDto'] => ({
    id: 'thread-1',
    agentId: 'agent-1',
    agentName: 'assistant',
    title: 'Trip plan',
    archived: false,
    createdAt: '2026-10-25T10:00:00Z',
    lastMessageAt: '2026-10-25T11:58:00Z',
    preview: 'Here is the plan',
    pendingApprovals: 0,
    inFlight: false,
    queued: 0,
    ...over,
  }),
  message: (over: Partial<S['MessageDto']> = {}): S['MessageDto'] => ({
    id: 'm-1',
    role: 'user',
    content: 'hello',
    status: 'done',
    correlationId: null,
    createdAt: '2026-10-25T11:00:00Z',
    kind: 'message',
    seq: 1,
    toolCalls: null,
    toolCallId: null,
    toolOk: null,
    untrusted: false,
    truncated: false,
    ...over,
  }),
  approval: (over: Partial<S['ApprovalDetailDto']> = {}): S['ApprovalDetailDto'] => ({
    id: 'approval-1',
    agent: 'assistant',
    threadId: 'thread-1',
    correlationId: 'm-1',
    toolName: 'workspace_delete',
    summary: 'Delete notes/a.md',
    risk: 'high',
    reason: 'Deletes a file',
    status: 'pending',
    createdAt: '2026-10-25T11:59:00Z',
    expiresAt: '2026-10-25T12:04:00Z',
    decidedBy: null,
    decidedAt: null,
    ...over,
  }),
};

/** Handlers every rendered app needs: a signed-in session, system info, no approvals. */
export const defaultHandlers = [
  http.get('*/api/auth/session', () =>
    HttpResponse.json({
      authenticated: true,
      method: 'session',
      user: { id: 'u-1', name: 'kevin' },
      csrfToken: 'csrf-123',
      expiresAt: '2026-11-01T12:00:00Z',
    }),
  ),
  http.get('*/api/system', () =>
    HttpResponse.json({
      version: 'abc1234',
      provider: 'Fake',
      utcNow: NOW,
      budgetResetAtUtc: '2026-10-26T00:00:00Z',
      models: [],
    }),
  ),
  http.get('*/api/approvals', () => HttpResponse.json({ approvals: [] })),
  http.get('*/api/agents', () => HttpResponse.json({ agents: [fixtures.agent()] })),
  http.get('*/api/threads', () => HttpResponse.json({ threads: [], nextCursor: null })),
  http.get('*/api/usage', ({ request }) =>
    HttpResponse.json(fixtures.usage((new URL(request.url).searchParams.get('range') ?? 'today') as Range)),
  ),
  http.get('*/api/activity', () => HttpResponse.json({ items: [], nextCursor: null })),
  http.get('*/api/credits', () =>
    HttpResponse.json({
      status: 'unsupported',
      remainingUsd: null,
      totalCreditsUsd: null,
      totalUsageUsd: null,
      asOf: null,
      lastAttemptAt: null,
      lowThresholdUsd: 5,
    }),
  ),
];

export const server = setupServer(...defaultHandlers);

export function apiError(status: number, code: string, message = code, headers: Record<string, string> = {}) {
  return HttpResponse.json({ error: { code, message } }, { status, headers });
}
