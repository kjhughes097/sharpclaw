import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { components } from '../src/api/schema';
import { failReasonMessage } from '../src/api/errors';
import { TracePanel } from '../src/features/audit/TracePanel';
import { askLink } from '../src/features/chat/ToolActivity';
import { fixtures, server } from './msw';
import { renderApp, renderWithProviders } from './utils';

type S = components['schemas'];
const LINKED = 'thread-linked';
const ORIGIN = 'thread-origin';

/** A thread the app shows at `/prompts/:id`, with its messages and an optional list row. */
function serveThread(
  thread: Partial<S['ThreadDto']>,
  messages: S['MessageDto'][],
  listRow: Partial<S['ThreadSummaryDto']> = {},
) {
  server.use(
    http.get('*/api/threads/:id', () =>
      HttpResponse.json({
        thread: {
          id: LINKED,
          agentId: 'agent-1',
          title: 'From ade: What is 2+2?',
          createdAt: '2026-10-25T10:00:00Z',
          archived: false,
          lastMessageAt: '2026-10-25T11:00:00Z',
          context: { droppedTurns: 0, estimatedTokens: 100, windowTokens: 32000 },
          ...thread,
        },
        agent: fixtures.agent(),
        inFlight: null,
        queued: 0,
        pendingApprovals: [],
      } satisfies S['ThreadDetailResponse']),
    ),
    http.get('*/api/threads/:id/messages', () => HttpResponse.json({ messages, hasMoreBefore: false })),
    http.get('*/api/threads/:id/trace', () => HttpResponse.json({ turns: [] })),
    http.get('*/api/threads', () =>
      HttpResponse.json({ threads: [fixtures.thread({ id: LINKED, ...listRow })], nextCursor: null }),
    ),
  );
}

describe('agent asks (3.5)', () => {
  it('a linked thread names the asking agent, links back, and labels its question', async () => {
    serveThread(
      { originThreadId: ORIGIN, originAgentName: 'ade' },
      [
        fixtures.message({ id: 'u1', seq: 1, content: 'What is 2+2?', sentByAgent: 'ade', sentByUser: null }),
        fixtures.message({ id: 'r1', seq: 2, role: 'assistant', correlationId: 'u1', content: '4' }),
      ],
      { originThreadId: ORIGIN, originAgentName: 'ade' },
    );
    renderApp(`/prompts/${LINKED}`);

    const back = await screen.findByRole('link', { name: 'ade' });
    expect(back).toHaveAttribute('href', `/prompts/${ORIGIN}`);
    expect(screen.getByText(/asked by/)).toBeInTheDocument();
    expect(await screen.findByText(/from ade \(agent\)/i)).toBeInTheDocument();
    expect(screen.getByText('Agent ade said:')).toBeInTheDocument();
    const sessions = await screen.findByRole('list', { name: 'Sessions' });
    expect(within(sessions).getByText('from ade')).toBeInTheDocument();
  });

  it('an ask_agent tool row links to the conversation with the other agent', async () => {
    serveThread({ id: ORIGIN }, [
      fixtures.message({ id: 'u1', seq: 1, content: 'ask cody' }),
      fixtures.message({
        id: 'tr',
        seq: 2,
        role: 'assistant',
        kind: 'tool_request',
        correlationId: 'u1',
        content: '',
        toolCalls: [{ id: 'call_1', name: 'ask_agent', arguments: '{"agent":"cody","message":"2+2?"}' }],
      }),
      fixtures.message({
        id: 'res',
        seq: 3,
        role: 'tool',
        kind: 'tool_result',
        correlationId: 'u1',
        toolCallId: 'call_1',
        toolOk: true,
        untrusted: true,
        content: `{"ok":true,"agent":"cody","threadId":"${LINKED}","reply":"4","truncated":false}`,
      }),
      fixtures.message({ id: 'r1', seq: 4, role: 'assistant', correlationId: 'u1', content: 'Cody says 4' }),
    ]);
    renderApp(`/prompts/${ORIGIN}`);

    const link = await screen.findByRole('link', { name: 'Open the conversation with cody' });
    expect(link).toHaveAttribute('href', `/prompts/${LINKED}`);
  });

  it('askLink ignores other tools and unparseable rows', () => {
    const result = fixtures.message({ role: 'tool', kind: 'tool_result', content: '{"threadId":"t"}' });
    expect(askLink({ toolCallId: 'c', name: 'ask_agent', arguments: '{"agent":"cody"}', result })).toEqual({
      agent: 'cody',
      threadId: 't',
    });
    expect(
      askLink({ toolCallId: 'c', name: 'web_search', arguments: '{"agent":"cody"}', result }),
    ).toBeNull();
    expect(askLink({ toolCallId: 'c', name: 'ask_agent', arguments: 'not json', result })).toBeNull();
    expect(
      askLink({ toolCallId: 'c', name: 'ask_agent', arguments: '{"agent":"cody"}', result: null }),
    ).toBeNull();
  });

  it('the trace lists the agents a turn asked, with links and failures', async () => {
    server.use(
      http.get('*/api/threads/:id/trace', () =>
        HttpResponse.json({
          turns: [
            {
              correlationId: 'u1',
              userMessageId: 'u1',
              status: 'done',
              failReason: null,
              createdAt: '2026-10-25T11:50:00Z',
              claimedAt: null,
              completedAt: null,
              llmCalls: [],
              toolCalls: [],
              approvals: [],
              totals: { costUsd: 0, promptTokens: 0, completionTokens: 0, llmCalls: 0, toolCalls: 0 },
              asks: [
                {
                  toolCallId: 'call_1',
                  agentName: 'cody',
                  threadId: LINKED,
                  messageId: 'm1',
                  status: 'done',
                  failReason: null,
                },
                {
                  toolCallId: 'call_2',
                  agentName: 'bea',
                  threadId: 'thread-b',
                  messageId: 'm2',
                  status: 'failed',
                  failReason: 'ask_timeout',
                },
              ],
            },
          ],
        } satisfies S['TraceResponse']),
      ),
    );
    const { user } = renderWithProviders(
      <TracePanel threadId={ORIGIN} onClose={() => {}} onSelectTurn={() => {}} />,
    );
    await user.click(await screen.findByRole('button', { name: 'Details' }));

    expect(screen.getByRole('link', { name: 'cody' })).toHaveAttribute('href', `/prompts/${LINKED}`);
    expect(screen.getByRole('link', { name: 'bea' })).toHaveAttribute('href', '/prompts/thread-b');
    expect(screen.getByText(failReasonMessage('ask_timeout'))).toBeInTheDocument();
  });

  it('explains the ask fail reasons', () => {
    expect(failReasonMessage('ask_timeout')).toMatch(/stopped waiting/);
    expect(failReasonMessage('ask_cancelled')).toMatch(/turn ended/);
  });
});
