import { screen, waitFor, within } from '@testing-library/react';
import { axe } from 'jest-axe';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { components } from '../src/api/schema';
import { pickAgent } from '../src/features/prompts/PromptsPage';
import { fixtures, NOW, server } from './msw';
import { renderApp } from './utils';

type S = components['schemas'];
const T = 'thread-1';

function detail(model: string | null = null): S['ThreadDetailResponse'] {
  return {
    thread: {
      id: T,
      agentId: 'agent-1',
      title: 'Harness spec',
      createdAt: '2026-10-25T10:00:00Z',
      archived: false,
      lastMessageAt: '2026-10-25T11:00:00Z',
      context: { droppedTurns: 0, estimatedTokens: 404, windowTokens: 200000 },
      model,
    },
    agent: fixtures.agent(),
    inFlight: null,
    queued: 0,
    pendingApprovals: [],
  };
}

function withModels(models: string[]) {
  return http.get('*/api/system', () =>
    HttpResponse.json({
      version: 'abc1234',
      provider: 'Fake',
      utcNow: NOW,
      budgetResetAtUtc: '2026-10-26T00:00:00Z',
      models,
    }),
  );
}

function useThread(state: { detail: S['ThreadDetailResponse']; messages?: S['MessageDto'][] }) {
  server.use(
    http.get('*/api/threads/:id', () => HttpResponse.json(state.detail)),
    http.get('*/api/threads/:id/messages', () =>
      HttpResponse.json({ messages: state.messages ?? [], hasMoreBefore: false }),
    ),
    http.get('*/api/threads/:id/trace', () => HttpResponse.json({ turns: [] })),
  );
}

describe('prompts toolbar', () => {
  it('picks ?agent, else the thread agent, else the last used, else the first enabled', () => {
    const ade = fixtures.agent({ id: 'a', name: 'ade' });
    const cody = fixtures.agent({ id: 'c', name: 'cody' });
    const off = fixtures.agent({ id: 'o', name: 'aaa', enabled: false });
    const all = [cody, ade, off];
    expect(pickAgent(all, 'cody', ade, 'ade')?.name).toBe('cody');
    expect(pickAgent(all, null, cody, 'ade')?.name).toBe('cody');
    expect(pickAgent(all, null, undefined, 'cody')?.name).toBe('cody');
    expect(pickAgent(all, null, undefined, null)?.name).toBe('ade');
  });

  it('hides the model picker when no models are allowed', async () => {
    useThread({ detail: detail() });
    renderApp(`/prompts/${T}`);
    await screen.findByLabelText('Session status');
    expect(screen.queryByRole('combobox', { name: 'Model' })).toBeNull();
  });

  it("changes the open thread's model with a PATCH and says when it applies", async () => {
    let body: unknown;
    useThread({ detail: detail() });
    server.use(
      withModels(['alt/model', 'openai/gpt-test']),
      http.patch('*/api/threads/:id', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json({ thread: { ...detail('alt/model').thread } });
      }),
    );
    const { user } = renderApp(`/prompts/${T}`);
    const select = await screen.findByRole('combobox', { name: 'Model' });
    expect(select).toHaveValue('');
    // The agent's own model appears once, as the default.
    expect(
      within(select)
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual(['openai/gpt-test (default)', 'alt/model']);
    await user.selectOptions(select, 'alt/model');
    await waitFor(() => expect(body).toEqual({ title: null, archived: null, model: 'alt/model' }));
    expect(await screen.findByText('Model changes apply from the next reply.')).toBeInTheDocument();
  });

  it('shows the override in the picker and the status bar', async () => {
    useThread({ detail: detail('alt/model') });
    server.use(withModels(['alt/model']));
    renderApp(`/prompts/${T}`);
    expect(await screen.findByRole('combobox', { name: 'Model' })).toHaveValue('alt/model');
    const status = screen.getByLabelText('Session status');
    expect(status).toHaveTextContent('Model: alt/model');
    expect(status).toHaveTextContent('Context 404 / 200,000 (0.2%)');
    expect(status).toHaveTextContent('Session: Harness spec');
  });

  it('starts a new session with the chosen model', async () => {
    let created: unknown;
    server.use(
      withModels(['alt/model']),
      http.post('*/api/threads', async ({ request }) => {
        created = await request.json();
        return HttpResponse.json(
          { thread: { ...detail('alt/model').thread, id: 'thread-new' } },
          { status: 201 },
        );
      }),
    );
    useThread({ detail: detail('alt/model') });
    const { user } = renderApp('/prompts');
    await user.selectOptions(await screen.findByRole('combobox', { name: 'Model' }), 'alt/model');
    const sidebar = screen.getByRole('complementary', { name: 'Sessions' });
    await user.click(within(sidebar).getByRole('button', { name: '+ New session' }));
    await waitFor(() => expect(created).toEqual({ agentId: 'agent-1', title: null, model: 'alt/model' }));
  });

  it('lists the sessions of the agent from ?agent', async () => {
    const seen: (string | null)[] = [];
    server.use(
      http.get('*/api/agents', () =>
        HttpResponse.json({ agents: [fixtures.agent(), fixtures.agent({ id: 'agent-2', name: 'cody' })] }),
      ),
      http.get('*/api/threads', ({ request }) => {
        seen.push(new URL(request.url).searchParams.get('agentId'));
        return HttpResponse.json({ threads: [], nextCursor: null });
      }),
    );
    const { user } = renderApp('/prompts?agent=cody');
    const agentSelect = await screen.findByRole('combobox', { name: 'Agent' });
    await waitFor(() => expect(agentSelect).toHaveValue('cody'));
    await waitFor(() => expect(seen).toContain('agent-2'));
    await user.selectOptions(screen.getByRole('combobox', { name: 'Agent' }), 'assistant');
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/prompts?agent=assistant'));
    await waitFor(() => expect(seen).toContain('agent-1'));
  });
});

describe('prompts thread', () => {
  it('labels turns with the sender and the agent', async () => {
    useThread({
      detail: detail(),
      messages: [
        fixtures.message({ id: 'u1', seq: 1, content: "What's the weekly total?", sentByUser: 'ken' }),
        fixtures.message({ id: 'r1', seq: 2, role: 'assistant', correlationId: 'u1', content: '41.2 km' }),
      ],
    });
    const { container } = renderApp(`/prompts/${T}`);
    const list = await screen.findByRole('list', { name: 'Messages' });
    expect(await within(list).findByText(/^ken ·/)).toBeInTheDocument();
    expect(within(list).getByText(/^assistant ·/)).toBeInTheDocument();
    expect(await axe(container)).toHaveNoViolations();
  });

  it('opens the sessions drawer from the toolbar (phones)', async () => {
    server.use(
      http.get('*/api/threads', () => HttpResponse.json({ threads: [fixtures.thread()], nextCursor: null })),
    );
    const { user } = renderApp('/prompts');
    await user.click(await screen.findByRole('button', { name: 'Sessions' }));
    const drawer = await screen.findByRole('dialog', { name: 'Sessions' });
    await user.click(await within(drawer).findByRole('link', { name: /Trip plan/ }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(screen.getByTestId('location')).toHaveTextContent('/prompts/thread-1');
  });
});
