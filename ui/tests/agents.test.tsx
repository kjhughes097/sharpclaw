import { screen, waitFor, within } from '@testing-library/react';
import { axe } from 'jest-axe';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { apiError, fixtures, server } from './msw';
import { renderApp } from './utils';

// jest-axe's matcher is registered in tests/setup.ts; its types only cover Jest's `expect`.

const TOKEN = 'sc_agent_9f8e7d6c5b4a_secret_value';

function tokenMeta(over: Record<string, unknown> = {}) {
  return {
    id: 'tok-1',
    prefix: 'sc_agent',
    createdAt: '2026-10-25T10:00:00Z',
    expiresAt: null,
    lastUsedAt: '2026-10-25T11:59:00Z',
    revokedAt: null,
    ...over,
  };
}

function detailHandlers() {
  return [
    http.get('*/api/agents/:name', ({ params }) =>
      params.name === 'assistant' ? HttpResponse.json(fixtures.agent()) : apiError(404, 'not_found'),
    ),
    http.get('*/api/agents/:name/tokens', () =>
      HttpResponse.json({
        tokens: [
          tokenMeta(),
          tokenMeta({ id: 'tok-2', prefix: 'sc_old12', revokedAt: '2026-10-24T09:00:00Z' }),
        ],
      }),
    ),
    http.get('*/api/audit/llm-calls', ({ request }) => {
      const url = new URL(request.url);
      expect(url.searchParams.get('agent')).toBe('assistant');
      expect(url.searchParams.get('limit')).toBe('20');
      return HttpResponse.json({
        calls: [
          {
            id: 'call-1',
            agentId: 'agent-1',
            threadId: 'thread-1',
            correlationId: 'm-1',
            model: 'openai/gpt-test',
            status: 'ok',
            promptTokens: 1200,
            completionTokens: 85,
            costUsd: 0.0042,
            latencyMs: 1500,
            ttftMs: 300,
            errorCode: null,
            errorDetail: null,
            createdAt: '2026-10-25T11:58:00Z',
            purpose: 'chat',
            usageKnown: true,
          },
        ],
        nextCursor: null,
      });
    }),
  ];
}

/** Everything the query and mutation caches hold, as one string. */
function cacheDump(queryClient: ReturnType<typeof renderApp>['queryClient']) {
  return JSON.stringify([
    queryClient
      .getQueryCache()
      .getAll()
      .map((q) => q.state.data),
    queryClient
      .getMutationCache()
      .getAll()
      .map((m) => [m.state.data, m.state.variables]),
  ]);
}

describe('agents list', () => {
  it('shows spend, the reset time, context window and last-seen states', async () => {
    server.use(
      http.get('*/api/agents', () =>
        HttpResponse.json({
          agents: [
            fixtures.agent(),
            fixtures.agent({
              id: 'agent-2',
              name: 'stale-bot',
              lastSeenAt: '2026-10-25T11:50:00Z',
              contextWindowTokens: 64000,
            }),
            fixtures.agent({
              id: 'agent-3',
              name: 'newbie',
              lastSeenAt: null,
              todaySpendUsd: 0,
              dailyBudgetUsd: 2,
            }),
          ],
        }),
      ),
    );
    const { container } = renderApp('/agents');
    expect(await screen.findByRole('link', { name: 'assistant' })).toHaveAttribute(
      'href',
      '/agents/assistant',
    );
    expect(screen.getAllByText(/\$0\.25 of \$1\.00/)).toHaveLength(2);
    expect(screen.getByText(/\$0\.00 of \$2\.00/)).toBeInTheDocument();
    expect(screen.getAllByText(/resets at 00:00 UTC \(\d\d:\d\d.*local\)/)).toHaveLength(3);
    expect(screen.getByText('64000')).toBeInTheDocument();
    expect(screen.getAllByText('default')).toHaveLength(2);
    expect(screen.getByText('never')).toBeInTheDocument();
    // Only the agent last seen 10 minutes before the server's "now" is flagged.
    await waitFor(() => expect(screen.getAllByText(/Not seen recently/)).toHaveLength(1));
    const stale = screen.getByText(/Not seen recently/).closest('li')!;
    expect(within(stale).getByRole('link', { name: 'stale-bot' })).toBeInTheDocument();
    expect(
      screen.getByText("Persona and tool policy are managed in the agent's config files."),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /delete/i })).not.toBeInTheDocument();
    expect(await axe(container)).toHaveNoViolations();
  });

  it('shows the empty state', async () => {
    server.use(http.get('*/api/agents', () => HttpResponse.json({ agents: [] })));
    renderApp('/agents');
    expect(
      await screen.findByText('No agents yet. Create one to get a token for its container.'),
    ).toBeInTheDocument();
  });

  it('shows an error with retry', async () => {
    let fail = true;
    server.use(
      http.get('*/api/agents', () =>
        fail ? apiError(500, 'internal_error') : HttpResponse.json({ agents: [fixtures.agent()] }),
      ),
    );
    const { user } = renderApp('/agents');
    expect(await screen.findByText('Something went wrong. Try again.')).toBeInTheDocument();
    fail = false;
    await user.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByRole('link', { name: 'assistant' })).toBeInTheDocument();
  });

  it('toggles enabled with a PATCH that leaves other fields null', async () => {
    let body: unknown;
    server.use(
      http.patch('*/api/agents/:name', async ({ request, params }) => {
        expect(params.name).toBe('assistant');
        body = await request.json();
        return HttpResponse.json(fixtures.agent({ enabled: false }));
      }),
    );
    const { user } = renderApp('/agents');
    const toggle = await screen.findByRole('switch', { name: 'Enabled: assistant' });
    expect(toggle).toHaveAttribute('aria-checked', 'true');
    await user.click(toggle);
    await waitFor(() => expect(body).toEqual({ model: null, dailyBudgetUsd: null, enabled: false }));
  });
});

describe('create agent', () => {
  it('validates the name on the client', async () => {
    let posted = false;
    server.use(
      http.post('*/api/agents', () => {
        posted = true;
        return HttpResponse.json({});
      }),
    );
    const { user } = renderApp('/agents');
    await user.click(await screen.findByRole('button', { name: 'New agent' }));
    const dialog = await screen.findByRole('dialog', { name: 'New agent' });
    await user.type(within(dialog).getByLabelText('Name'), 'Bad_Name');
    await user.type(within(dialog).getByLabelText('Model'), 'openai/gpt-4o-mini');
    await user.type(within(dialog).getByLabelText('Context window (optional)'), '50');
    await user.click(within(dialog).getByRole('button', { name: 'Create agent' }));
    expect(within(dialog).getByLabelText('Name')).toHaveAttribute('aria-invalid', 'true');
    expect(
      within(dialog).getAllByText(/Lowercase letters, digits and hyphens; 2–32 characters/),
    ).toHaveLength(2);
    expect(within(dialog).getByLabelText('Context window (optional)')).toHaveAttribute(
      'aria-invalid',
      'true',
    );
    expect(within(dialog).getByText(/OpenRouter model id/)).toBeInTheDocument();
    expect(posted).toBe(false);
  });

  it('shows "Already exists." next to the name on 409', async () => {
    server.use(http.post('*/api/agents', () => apiError(409, 'conflict', 'Agent exists')));
    const { user } = renderApp('/agents');
    await user.click(await screen.findByRole('button', { name: 'New agent' }));
    const dialog = await screen.findByRole('dialog', { name: 'New agent' });
    await user.type(within(dialog).getByLabelText('Name'), 'assistant');
    await user.type(within(dialog).getByLabelText('Model'), 'openai/gpt-4o-mini');
    await user.click(within(dialog).getByRole('button', { name: 'Create agent' }));
    expect(await within(dialog).findByText('Already exists.')).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Name')).toHaveAttribute('aria-invalid', 'true');
  });

  it('shows server validation errors in the dialog', async () => {
    server.use(http.post('*/api/agents', () => apiError(400, 'validation_failed', 'Model is not allowed.')));
    const { user } = renderApp('/agents');
    await user.click(await screen.findByRole('button', { name: 'New agent' }));
    const dialog = await screen.findByRole('dialog', { name: 'New agent' });
    await user.type(within(dialog).getByLabelText('Name'), 'helper');
    await user.type(within(dialog).getByLabelText('Model'), 'nope');
    await user.click(within(dialog).getByRole('button', { name: 'Create agent' }));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Model is not allowed.');
  });

  it('reveals the token once and forgets it after closing', async () => {
    let body: unknown;
    server.use(
      http.post('*/api/agents', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json(
          {
            agent: fixtures.agent({ id: 'agent-9', name: 'helper' }),
            token: { id: 'tok-9', prefix: 'sc_agent', value: TOKEN, expiresAt: null },
          },
          { status: 201 },
        );
      }),
    );
    const { user, queryClient } = renderApp('/agents');
    await user.click(await screen.findByRole('button', { name: 'New agent' }));
    const form = await screen.findByRole('dialog', { name: 'New agent' });
    await user.type(within(form).getByLabelText('Name'), 'helper');
    await user.type(within(form).getByLabelText('Model'), 'openai/gpt-4o-mini');
    await user.clear(within(form).getByLabelText('Daily budget (USD)'));
    await user.type(within(form).getByLabelText('Daily budget (USD)'), '2.5');
    await user.type(within(form).getByLabelText('Context window (optional)'), '64000');
    await user.click(within(form).getByRole('button', { name: 'Create agent' }));

    const reveal = await screen.findByRole('dialog', { name: 'Agent token' });
    expect(body).toEqual({
      name: 'helper',
      model: 'openai/gpt-4o-mini',
      dailyBudgetUsd: 2.5,
      contextWindowTokens: 64000,
    });
    expect(screen.queryByRole('dialog', { name: 'New agent' })).not.toBeInTheDocument();
    expect(within(reveal).getByLabelText('Agent token')).toHaveTextContent(TOKEN);
    expect(
      within(reveal).getByText(
        'This token will not be shown again. Store it now — the agent needs it as AGENT_TOKEN.',
      ),
    ).toBeInTheDocument();

    // navigator.clipboard is undefined here: Copy uses the fallback and never throws.
    await user.click(within(reveal).getByRole('button', { name: 'Copy' }));
    expect(within(reveal).getByText(/Copied\.|Copy failed/)).toBeInTheDocument();

    // Not dismissible by Esc, and Close waits for the confirmation.
    await user.keyboard('{Escape}');
    expect(screen.getByRole('dialog', { name: 'Agent token' })).toBeInTheDocument();
    const close = within(reveal).getByRole('button', { name: 'Close' });
    expect(close).toBeDisabled();
    await user.click(within(reveal).getByLabelText("I've saved it"));
    expect(close).toBeEnabled();
    await user.click(close);

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(document.body.textContent).not.toContain(TOKEN);
    await waitFor(() => expect(cacheDump(queryClient)).not.toContain(TOKEN));
    expect(window.location.href).not.toContain(TOKEN);
    expect(screen.getByTestId('location')).not.toHaveTextContent(TOKEN);
  });
});

describe('agent detail', () => {
  it('shows settings, tokens and recent LLM calls', async () => {
    server.use(...detailHandlers());
    const { container } = renderApp('/agents/assistant');
    expect(await screen.findByRole('heading', { level: 1, name: 'assistant' })).toBeInTheDocument();
    expect(screen.getByLabelText('Model')).toHaveValue('openai/gpt-test');
    expect(await screen.findByText('sc_old12')).toBeInTheDocument();
    expect(screen.getByText('revoked')).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: /^Revoke token/ })).toHaveLength(1);
    expect(await screen.findByText('$0.0042')).toBeInTheDocument();
    expect(screen.getByText('1200 / 85')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'View in Audit' })).toHaveAttribute(
      'href',
      '/audit?tab=llm&agent=assistant',
    );
    expect(
      screen.getByText("Persona and tool policy are managed in the agent's config files."),
    ).toBeInTheDocument();
    expect(await axe(container)).toHaveNoViolations();
  });

  it('saves model, budget and context window', async () => {
    let body: unknown;
    server.use(
      ...detailHandlers(),
      http.patch('*/api/agents/:name', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json(
          fixtures.agent({ model: 'openai/gpt-4o', dailyBudgetUsd: 3, contextWindowTokens: 32000 }),
        );
      }),
    );
    const { user } = renderApp('/agents/assistant');
    const model = await screen.findByLabelText('Model');
    await user.clear(model);
    await user.type(model, 'openai/gpt-4o');
    await user.clear(screen.getByLabelText('Daily budget (USD)'));
    await user.type(screen.getByLabelText('Daily budget (USD)'), '3');
    await user.type(screen.getByLabelText('Context window'), '32000');
    await user.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() =>
      expect(body).toEqual({
        model: 'openai/gpt-4o',
        dailyBudgetUsd: 3,
        enabled: null,
        contextWindowTokens: 32000,
      }),
    );
    expect(await screen.findByText('Saved.')).toBeInTheDocument();
  });

  it('revokes a token after confirmation', async () => {
    let deleted: string | null = null;
    server.use(
      ...detailHandlers(),
      http.delete('*/api/agents/:name/tokens/:tokenId', ({ params }) => {
        deleted = String(params.tokenId);
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const { user } = renderApp('/agents/assistant');
    await user.click(await screen.findByRole('button', { name: 'Revoke token sc_agent' }));
    const confirm = await screen.findByRole('dialog', { name: 'Revoke token?' });
    expect(deleted).toBeNull();
    await user.click(within(confirm).getByRole('button', { name: 'Revoke' }));
    await waitFor(() => expect(deleted).toBe('tok-1'));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('issues a token with an expiry and reveals it once', async () => {
    let body: { expiresAt: string | null } | undefined;
    server.use(
      ...detailHandlers(),
      http.post('*/api/agents/:name/tokens', async ({ request }) => {
        body = (await request.json()) as { expiresAt: string | null };
        return HttpResponse.json(
          { token: { id: 'tok-3', prefix: 'sc_agent', value: TOKEN, expiresAt: body.expiresAt } },
          { status: 201 },
        );
      }),
    );
    const { user, queryClient } = renderApp('/agents/assistant');
    await user.type(await screen.findByLabelText('Expires on (optional)'), '2099-01-31');
    await user.click(screen.getByRole('button', { name: 'Issue token' }));
    const reveal = await screen.findByRole('dialog', { name: 'Agent token' });
    expect(body?.expiresAt).toMatch(/^2099-0[12]-\d\dT/);
    expect(within(reveal).getByLabelText('Agent token')).toHaveTextContent(TOKEN);
    await user.click(within(reveal).getByLabelText("I've saved it"));
    await user.click(within(reveal).getByRole('button', { name: 'Close' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(document.body.textContent).not.toContain(TOKEN);
    await waitFor(() => expect(cacheDump(queryClient)).not.toContain(TOKEN));
  });

  it('shows not found with a link back', async () => {
    server.use(...detailHandlers());
    renderApp('/agents/ghost');
    expect(await screen.findByText('Not found. It may have been removed.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Back to agents' })).toHaveAttribute('href', '/agents');
  });
});
