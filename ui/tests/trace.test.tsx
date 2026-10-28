import { screen, waitFor, within } from '@testing-library/react';
import { axe } from 'jest-axe';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import type { components } from '../src/api/schema';
import { TracePanel } from '../src/features/audit/TracePanel';
import { apiError, server } from './msw';
import { renderWithProviders } from './utils';

type S = components['schemas'];

function turn(over: Partial<S['TraceTurnDto']> = {}): S['TraceTurnDto'] {
  return {
    correlationId: 'corr-1',
    userMessageId: 'msg-user-1',
    status: 'done',
    failReason: null,
    createdAt: '2026-10-25T11:50:00Z',
    claimedAt: '2026-10-25T11:50:01Z',
    completedAt: '2026-10-25T11:50:03.500Z',
    llmCalls: [
      {
        id: 'llm-1',
        model: 'openai/gpt-test',
        status: 'ok',
        promptTokens: 1200,
        completionTokens: 300,
        estimatedPromptTokens: 1100,
        costUsd: 0.0042,
        usageKnown: true,
        latencyMs: 900,
        ttftMs: 250,
        errorCode: null,
        purpose: 'chat',
        createdAt: '2026-10-25T11:50:01Z',
      },
    ],
    toolCalls: [
      {
        id: 'tc-1',
        toolCallId: 'call_1',
        toolName: 'workspace_delete',
        risk: 'high',
        decision: 'approved',
        outcome: 'succeeded',
        approvalId: 'approval-1',
        durationMs: 80,
        createdAt: '2026-10-25T11:50:02Z',
        reportedAt: '2026-10-25T11:50:02Z',
      },
    ],
    approvals: [
      {
        id: 'approval-1',
        toolCallId: 'call_1',
        toolName: 'workspace_delete',
        risk: 'high',
        status: 'approved',
        createdAt: '2026-10-25T11:50:02Z',
        decidedAt: '2026-10-25T11:50:02Z',
        decidedBy: 'kevin',
      },
    ],
    totals: { costUsd: 0.0042, promptTokens: 1200, completionTokens: 300, llmCalls: 1, toolCalls: 1 },
    ...over,
  };
}

function serveTrace(turns: S['TraceTurnDto'][]) {
  const calls: string[] = [];
  server.use(
    http.get('*/api/threads/:id/trace', ({ params }) => {
      calls.push(String(params.id));
      return HttpResponse.json({ turns });
    }),
  );
  return calls;
}

function renderPanel(props: Partial<{ onClose: () => void; onSelectTurn: (id: string) => void }> = {}) {
  const onClose = props.onClose ?? vi.fn();
  const onSelectTurn = props.onSelectTurn ?? vi.fn();
  const utils = renderWithProviders(
    <TracePanel threadId="thread-1" onClose={onClose} onSelectTurn={onSelectTurn} />,
  );
  return { ...utils, onClose, onSelectTurn };
}

describe('trace panel', () => {
  it('renders one entry per turn with status, duration and totals', async () => {
    const calls = serveTrace([
      turn(),
      turn({
        correlationId: 'corr-2',
        userMessageId: 'msg-user-2',
        status: 'failed',
        failReason: 'budget_exceeded',
        completedAt: null,
        llmCalls: [],
        toolCalls: [],
        approvals: [],
        totals: { costUsd: 0.00001, promptTokens: 0, completionTokens: 0, llmCalls: 0, toolCalls: 0 },
      }),
    ]);
    const { container } = renderPanel();
    expect(screen.getByRole('heading', { name: 'Trace' })).toBeInTheDocument();
    const items = await screen.findAllByRole('listitem');
    expect(calls).toEqual(['thread-1']);
    expect(items).toHaveLength(2);
    expect(items[0]).toHaveTextContent('Turn 1');
    expect(items[0]).toHaveTextContent('done');
    expect(items[0]).toHaveTextContent('2.5 s');
    expect(items[0]).toHaveTextContent('$0.0042');
    expect(items[0]).toHaveTextContent('1.2k / 300');
    expect(items[0]).toHaveTextContent('1 LLM · 1 tool');
    expect(items[1]).toHaveTextContent('failed');
    expect(items[1]).toHaveTextContent('—'); // no completedAt → no duration
    expect(items[1]).toHaveTextContent('<$0.0001');
    // Metadata only: no message content anywhere.
    expect(container).not.toHaveTextContent('hello');
    expect(await axe(container)).toHaveNoViolations();
  });

  it('expands a turn to its LLM calls, tool calls and approvals', async () => {
    serveTrace([turn()]);
    const { user, container } = renderPanel();
    const toggle = await screen.findByRole('button', { name: 'Details' });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByText('workspace_delete')).not.toBeInTheDocument();

    await user.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    const details = document.getElementById(toggle.getAttribute('aria-controls')!)!;
    expect(details).toBeVisible();
    expect(within(details).getByText('openai/gpt-test')).toBeInTheDocument();
    expect(details).toHaveTextContent('900 ms');
    expect(details).toHaveTextContent('250 ms');
    expect(within(details).getAllByText('workspace_delete')).toHaveLength(2);
    expect(details).toHaveTextContent('risk: high');
    expect(details).toHaveTextContent('succeeded');
    expect(details).toHaveTextContent('80 ms');
    expect(details).toHaveTextContent('kevin');
    expect(await axe(container)).toHaveNoViolations();

    await user.click(screen.getByRole('button', { name: 'Hide details' }));
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
  });

  it('calls onSelectTurn with the user message id and onClose from the close button', async () => {
    serveTrace([turn(), turn({ correlationId: 'corr-2', userMessageId: 'msg-user-2' })]);
    const { user, onSelectTurn, onClose } = renderPanel();
    const buttons = await screen.findAllByRole('button', { name: 'Show in chat' });
    await user.click(buttons[1]!);
    expect(onSelectTurn).toHaveBeenCalledWith('msg-user-2');
    await user.click(screen.getByRole('button', { name: 'Close trace' }));
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('shows an empty state', async () => {
    serveTrace([]);
    renderPanel();
    expect(await screen.findByText('No turns yet.')).toBeInTheDocument();
  });

  it('shows an error with retry', async () => {
    let fail = true;
    server.use(
      http.get('*/api/threads/:id/trace', () =>
        fail ? apiError(500, 'internal_error') : HttpResponse.json({ turns: [turn()] }),
      ),
    );
    const { user } = renderPanel();
    expect(await screen.findByRole('alert')).toBeInTheDocument();
    fail = false;
    await user.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(screen.getByText('Turn 1')).toBeInTheDocument());
  });
});
