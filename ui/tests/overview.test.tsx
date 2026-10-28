import { act, screen, waitFor, within } from '@testing-library/react';
import { axe } from 'jest-axe';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { components } from '../src/api/schema';
import { topModels } from '../src/features/overview/charts';
import { formatMoney } from '../src/lib/time';
import { fixtures, server } from './msw';
import { renderApp } from './utils';

type S = components['schemas'];

function credits(over: Partial<S['CreditsResponse']>): S['CreditsResponse'] {
  return {
    status: 'ok',
    remainingUsd: 3.36,
    totalCreditsUsd: 20,
    totalUsageUsd: 16.64,
    asOf: '2026-10-25T06:00:00Z',
    lastAttemptAt: '2026-10-25T06:00:00Z',
    lowThresholdUsd: 5,
    ...over,
  };
}

function activityItem(over: Partial<S['ActivityItemDto']> = {}): S['ActivityItemDto'] {
  return {
    messageId: 'm-1',
    threadId: 'thread-1',
    threadTitle: 'Trip plan',
    agentId: 'agent-1',
    agentName: 'assistant',
    role: 'user',
    from: 'kevin',
    to: 'assistant',
    status: 'done',
    createdAt: '2026-10-25T11:30:41Z',
    preview: 'ping **from** kevin',
    ...over,
  };
}

describe('overview', () => {
  it('renders the three usage charts with their bucket counts and text alternatives', async () => {
    server.use(
      http.get('*/api/usage', ({ request }) => {
        const range = new URL(request.url).searchParams.get('range') as 'today' | '7d' | '30d';
        const base = fixtures.usage(range);
        if (range !== '7d') return HttpResponse.json(base);
        base.buckets[27] = {
          ...base.buckets[27]!,
          calls: 1,
          promptTokens: 3800,
          completionTokens: 292,
          costUsd: 0.006,
        };
        return HttpResponse.json({
          ...base,
          totals: {
            calls: 1,
            promptTokens: 3800,
            completionTokens: 292,
            costUsd: 0.006,
            unknownUsageCalls: 0,
          },
          byModel: [
            { model: 'gpt-a-mini', calls: 1, promptTokens: 3800, completionTokens: 292, costUsd: 0.006 },
          ],
        });
      }),
    );
    const { container } = renderApp('/overview');
    const week = await screen.findByRole('img', { name: 'Last 7 days: 4,092 tokens, <$0.01' });
    expect(week.querySelectorAll('[data-testid="usage-bar"]')).toHaveLength(28);
    expect(
      screen
        .getByRole('img', { name: 'Today: 0 tokens, $0.00' })
        .querySelectorAll('[data-testid="usage-bar"]'),
    ).toHaveLength(13);
    expect(
      screen
        .getByRole('img', { name: 'Last 30 days: 0 tokens, $0.00' })
        .querySelectorAll('[data-testid="usage-bar"]'),
    ).toHaveLength(30);
    expect(screen.getByText('Total cost, 7 days').nextElementSibling).toHaveTextContent('<$0.01');
    expect(
      within(screen.getByRole('list', { name: 'Cost by model' })).getByText('gpt-a-mini'),
    ).toBeInTheDocument();
    expect(await axe(container)).toHaveNoViolations();
  });

  it('formats money and folds extra models into "other"', () => {
    expect(formatMoney(0)).toBe('$0.00');
    expect(formatMoney(0.004)).toBe('<$0.01');
    expect(formatMoney(3.356)).toBe('$3.36');
    const models = Array.from({ length: 8 }, (_, i) => ({
      model: `m${i}`,
      calls: 1,
      promptTokens: 0,
      completionTokens: 0,
      costUsd: 8 - i,
    }));
    const top = topModels(models);
    expect(top.map((m) => m.model)).toEqual(['m0', 'm1', 'm2', 'm3', 'm4', 'm5', 'other']);
    expect(top[6]!.costUsd).toBe(3);
  });

  it.each([
    [credits({}), '$3.36', null, null],
    [credits({ remainingUsd: 1.5 }), '$1.50', null, null],
    [credits({ status: 'stale' }), '$3.36', 'stale', null],
    [credits({ status: 'failing' }), '$3.36', null, 'daily fetch failing'],
    [credits({ status: 'not_configured', remainingUsd: null }), null, null, 'OpenRouter__ManagementKey'],
    [
      credits({ status: 'unsupported', remainingUsd: null }),
      null,
      null,
      'Not available with the fake provider.',
    ],
  ])('shows the credits card for %o', async (response, amount, caption, text) => {
    server.use(http.get('*/api/credits', () => HttpResponse.json(response)));
    renderApp('/overview');
    const card = (await screen.findByRole('heading', { name: 'Credits Remaining' })).closest('section')!;
    await waitFor(() => expect(within(card).queryByRole('status', { name: 'Loading credits' })).toBeNull());
    if (amount) expect(within(card).getByTestId('credits-amount')).toHaveTextContent(amount);
    else expect(within(card).queryByTestId('credits-amount')).toBeNull();
    if (caption) expect(within(card).getByText(caption)).toBeInTheDocument();
    if (text) expect(within(card).getByText(text)).toBeInTheDocument();
    if (response.remainingUsd !== null && response.remainingUsd < 5)
      expect(within(card).getByTestId('credits-amount')).toHaveClass('text-danger');
  });

  it('shows a card per agent with its usage and messages, and a row opens the thread', async () => {
    server.use(
      http.get('*/api/activity', () =>
        HttpResponse.json({
          items: [
            activityItem({
              messageId: 'r',
              role: 'assistant',
              from: 'assistant',
              to: 'kevin',
              preview: 'pong',
            }),
            activityItem(),
          ],
          nextCursor: null,
        }),
      ),
      http.get('*/api/usage', ({ request }) => {
        const range = new URL(request.url).searchParams.get('range') as 'today' | '7d' | '30d';
        return HttpResponse.json(
          fixtures.usage(range, {
            byAgent: [
              {
                agentId: 'agent-1',
                agentName: 'assistant',
                calls: 2,
                promptTokens: 4000,
                completionTokens: 92,
                costUsd: 0.01,
              },
            ],
          }),
        );
      }),
    );
    const { user } = renderApp('/overview');
    const card = await screen.findByRole('region', { name: 'Agent assistant' });
    expect(within(card).getByText('openai/gpt-test')).toBeInTheDocument();
    expect(within(card).getByText('Last 7 days').nextElementSibling).toHaveTextContent('4,092 tokens $0.01');
    const rows = await within(card).findAllByRole('link', { name: /→/ });
    expect(rows[0]).toHaveTextContent(/assistant\s*→\s*kevin/);
    expect(rows[0]).toHaveTextContent('reply');
    // Previews are plain text, never markdown.
    expect(within(card).getByText('ping **from** kevin')).toBeInTheDocument();
    await user.click(rows[1]!);
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/prompts/thread-1'));
  });

  it('refetches messages on events, and pause holds them back until resume', async () => {
    let calls = 0;
    server.use(
      http.get('*/api/activity', () => {
        calls++;
        return HttpResponse.json({ items: [], nextCursor: null });
      }),
    );
    const { user, services } = renderApp('/overview');
    await screen.findByRole('region', { name: 'Agent assistant' });
    await waitFor(() => expect(calls).toBe(1));
    const emit = (id: number) =>
      act(() =>
        services
          .source()
          .emit('message.created', { messageId: `m${id}`, threadId: 't', correlationId: `m${id}` }, id),
      );

    emit(1);
    emit(2);
    await waitFor(() => expect(calls).toBe(2), { timeout: 3000 });

    await user.click(screen.getByRole('button', { name: 'pause' }));
    emit(3);
    expect(await screen.findByText(/Paused · 1 new/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'resume' }));
    await waitFor(() => expect(calls).toBe(3));
    expect(screen.getByText('Live messages')).toBeInTheDocument();
  });
});
