import { screen, waitFor, within } from '@testing-library/react';
import { axe } from 'jest-axe';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { components } from '../src/api/schema';
import { isUnreported, sinceFor } from '../src/features/audit/auditFormat';
import { NOW, apiError, server } from './msw';
import { renderApp } from './utils';

type S = components['schemas'];

const ARGS_SENTINEL = 'SENTINEL-ARGUMENTS-must-not-render';

function llmCall(over: Partial<S['LlmCallDto']> = {}): S['LlmCallDto'] {
  return {
    id: 'llm-1',
    agentId: 'agent-1',
    threadId: 'thread-abcdef123456',
    correlationId: 'corr-1',
    model: 'openai/gpt-test',
    status: 'ok',
    promptTokens: 1200,
    completionTokens: 300,
    estimatedPromptTokens: 1150,
    costUsd: 0.0042,
    usageKnown: true,
    latencyMs: 1500,
    ttftMs: 400,
    errorCode: null,
    errorDetail: null,
    purpose: 'chat',
    createdAt: '2026-10-25T11:59:00Z',
    ...over,
  };
}

function toolCall(over: Partial<S['ToolCallDto']> = {}): S['ToolCallDto'] {
  return {
    id: 'tool-1',
    agentId: 'agent-1',
    threadId: 'thread-abcdef123456',
    correlationId: 'corr-1',
    llmCallId: 'llm-1',
    toolCallId: 'call_1',
    toolName: 'workspace_write',
    arguments: JSON.stringify({ path: 'notes/a.md', content: ARGS_SENTINEL }),
    risk: 'medium',
    decision: 'auto_approved',
    status: 'succeeded',
    approvalId: null,
    durationMs: 120,
    errorCode: null,
    tainted: false,
    createdAt: '2026-10-25T11:59:00Z',
    reportedAt: '2026-10-25T11:59:01Z',
    ...over,
  };
}

/** Records every request URL so tests can assert the query the page sent. */
function captureLlm(
  respond: (url: URL) => S['AuditResponse'] = () => ({ calls: [llmCall()], nextCursor: null }),
) {
  const urls: URL[] = [];
  server.use(
    http.get('*/api/audit/llm-calls', ({ request }) => {
      const url = new URL(request.url);
      urls.push(url);
      return HttpResponse.json(respond(url));
    }),
  );
  return urls;
}

function captureTools(
  respond: (url: URL) => S['ToolCallAuditResponse'] = () => ({ calls: [toolCall()], nextCursor: null }),
) {
  const urls: URL[] = [];
  server.use(
    http.get('*/api/audit/tool-calls', ({ request }) => {
      const url = new URL(request.url);
      urls.push(url);
      return HttpResponse.json(respond(url));
    }),
  );
  return urls;
}

describe('audit page', () => {
  it('shows LLM calls by default with their metadata columns', async () => {
    captureLlm();
    const { container } = renderApp('/audit');
    const table = await screen.findByRole('table', { name: 'LLM calls' });
    expect(screen.getByRole('tab', { name: 'LLM calls' })).toHaveAttribute('aria-selected', 'true');
    const row = within(table).getAllByRole('row')[1]!;
    expect(row).toHaveTextContent('assistant'); // agentId mapped to its name
    expect(row).toHaveTextContent('openai/gpt-test');
    expect(row).toHaveTextContent('ok');
    expect(row).toHaveTextContent('1.2k / 300');
    expect(row).toHaveTextContent('$0.0042');
    expect(row).toHaveTextContent('1.5 s');
    expect(row).toHaveTextContent('400 ms');
    expect(within(row).getByRole('link', { name: /thread/i })).toHaveAttribute(
      'href',
      '/prompts/thread-abcdef123456?panel=trace',
    );
    expect(await axe(container)).toHaveNoViolations();
  });

  it('switches tabs through the URL', async () => {
    captureLlm();
    const toolUrls = captureTools();
    const { user } = renderApp('/audit?status=error');
    await screen.findByRole('table', { name: 'LLM calls' });
    await user.click(screen.getByRole('tab', { name: 'Tool calls' }));
    await screen.findByRole('table', { name: 'Tool calls' });
    // The status filter is per tab, so it is dropped.
    expect(screen.getByTestId('location')).toHaveTextContent('/audit?tab=tools');
    expect(screen.getByTestId('location')).not.toHaveTextContent('status');
    expect(toolUrls[0]!.searchParams.get('status')).toBeNull();
  });

  it('opens the tool tab from a deep link', async () => {
    captureTools();
    renderApp('/audit?tab=tools');
    expect(await screen.findByRole('table', { name: 'Tool calls' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Tool calls' })).toHaveAttribute('aria-selected', 'true');
  });

  it('sends the filters as query parameters and keeps them in the URL', async () => {
    const urls = captureLlm();
    const { user } = renderApp('/audit');
    await screen.findByRole('table', { name: 'LLM calls' });
    expect(urls[0]!.searchParams.get('limit')).toBe('100');
    expect(urls[0]!.searchParams.get('agent')).toBeNull();

    await user.selectOptions(screen.getByLabelText('Agent'), 'assistant');
    await waitFor(() => expect(urls.at(-1)!.searchParams.get('agent')).toBe('assistant'));
    await user.selectOptions(screen.getByLabelText('Status'), 'error');
    await waitFor(() => expect(urls.at(-1)!.searchParams.get('status')).toBe('error'));
    await user.selectOptions(screen.getByLabelText('Time window'), '1h');
    await waitFor(() => expect(urls.at(-1)!.searchParams.get('since')).not.toBeNull());
    const since = Date.parse(urls.at(-1)!.searchParams.get('since')!);
    expect(since).toBeGreaterThanOrEqual(Date.parse(NOW) - 3_600_000);
    expect(screen.getByTestId('location')).toHaveTextContent('agent=assistant');
    expect(screen.getByTestId('location')).toHaveTextContent('status=error');
    expect(screen.getByTestId('location')).toHaveTextContent('window=1h');
  });

  it('restores filters from the URL', async () => {
    const urls = captureLlm();
    renderApp('/audit?agent=assistant&status=rejected&window=7d');
    await screen.findByRole('table', { name: 'LLM calls' });
    expect(urls[0]!.searchParams.get('agent')).toBe('assistant');
    expect(urls[0]!.searchParams.get('status')).toBe('rejected');
    expect(urls[0]!.searchParams.get('since')).not.toBeNull();
    expect(screen.getByLabelText('Agent')).toHaveValue('assistant');
    expect(screen.getByLabelText('Status')).toHaveValue('rejected');
    expect(screen.getByLabelText('Time window')).toHaveValue('7d');
  });

  it('filters by thread from ?threadId= and removes the chip', async () => {
    const urls = captureLlm();
    const { user } = renderApp('/audit?threadId=0193abcd-ef01-7000-8000-000000000000');
    await screen.findByRole('table', { name: 'LLM calls' });
    expect(urls[0]!.searchParams.get('threadId')).toBe('0193abcd-ef01-7000-8000-000000000000');
    const chip = screen.getByRole('button', { name: /thread 0193abcd/i });
    expect(chip).toHaveTextContent('Thread 0193abcd');
    await user.click(chip);
    await waitFor(() => expect(urls.at(-1)!.searchParams.get('threadId')).toBeNull());
    expect(screen.queryByRole('button', { name: /thread 0193abcd/i })).not.toBeInTheDocument();
  });

  it('appends the next page with before=<cursor> and sums the loaded cost', async () => {
    const urls = captureLlm((url) =>
      url.searchParams.get('before') === 'cursor-1'
        ? { calls: [llmCall({ id: 'llm-3', model: 'model-page-2', costUsd: 0.003 })], nextCursor: null }
        : {
            calls: [llmCall({ id: 'llm-1', costUsd: 0.001 }), llmCall({ id: 'llm-2', costUsd: 0.002 })],
            nextCursor: 'cursor-1',
          },
    );
    const { user } = renderApp('/audit');
    const table = await screen.findByRole('table', { name: 'LLM calls' });
    expect(within(table).getAllByRole('row')).toHaveLength(3);
    expect(screen.getByText(/Total cost of loaded rows/)).toHaveTextContent('$0.0030');

    await user.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(within(table).getAllByRole('row')).toHaveLength(4));
    expect(urls.at(-1)!.searchParams.get('before')).toBe('cursor-1');
    expect(within(table).getAllByRole('row')[3]).toHaveTextContent('model-page-2');
    expect(screen.getByText(/Total cost of loaded rows/)).toHaveTextContent('$0.0060');
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
  });

  it('marks unreported tool calls older than five minutes and never shows arguments', async () => {
    captureTools(() => ({
      calls: [
        toolCall({
          id: 'old',
          toolName: 'tool_old_pending',
          createdAt: '2026-10-25T11:50:00Z',
          reportedAt: null,
          status: 'requested',
        }),
        toolCall({
          id: 'new',
          toolName: 'tool_new_pending',
          createdAt: '2026-10-25T11:58:00Z',
          reportedAt: null,
          status: 'requested',
        }),
        toolCall({
          id: 'done',
          toolName: 'tool_old_done',
          createdAt: '2026-10-25T11:50:00Z',
          approvalId: '0193aaaa-0000-7000-8000-000000000000',
        }),
      ],
      nextCursor: null,
    }));
    const { container } = renderApp('/audit?tab=tools');
    const table = await screen.findByRole('table', { name: 'Tool calls' });
    // Waits for the server clock (NOW) to replace the browser clock.
    await waitFor(() => expect(within(table).getAllByText('unreported')).toHaveLength(1));
    const rows = within(table).getAllByRole('row');
    expect(rows[1]).toHaveTextContent('unreported');
    expect(rows[2]).not.toHaveTextContent('unreported');
    expect(rows[3]).not.toHaveTextContent('unreported');
    expect(rows[3]).toHaveTextContent('0193aaaa');
    expect(rows[1]).toHaveTextContent('—');
    expect(rows[1]).toHaveTextContent('risk: medium');
    expect(container).not.toHaveTextContent(ARGS_SENTINEL);
    expect(container.innerHTML).not.toContain(ARGS_SENTINEL);
    expect(await axe(container)).toHaveNoViolations();
  });

  it('shows empty and error states with retry', async () => {
    let fail = true;
    server.use(
      http.get('*/api/audit/llm-calls', () =>
        fail ? apiError(500, 'internal_error') : HttpResponse.json({ calls: [], nextCursor: null }),
      ),
    );
    const { user } = renderApp('/audit');
    expect(await screen.findByRole('alert')).toBeInTheDocument();
    fail = false;
    await user.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('No LLM calls match these filters.')).toBeInTheDocument();
  });
});

describe('audit helpers', () => {
  const now = Date.parse(NOW);

  it('flags only unreported calls older than five minutes', () => {
    expect(isUnreported({ reportedAt: null, createdAt: '2026-10-25T11:54:59Z' }, now)).toBe(true);
    expect(isUnreported({ reportedAt: null, createdAt: '2026-10-25T11:55:00Z' }, now)).toBe(false);
    expect(isUnreported({ reportedAt: '2026-10-25T11:51:00Z', createdAt: '2026-10-25T11:50:00Z' }, now)).toBe(
      false,
    );
  });

  it('computes since from the server clock', () => {
    expect(sinceFor('1h', now)).toBe('2026-10-25T11:00:00.000Z');
    expect(sinceFor('24h', now)).toBe('2026-10-24T12:00:00.000Z');
    expect(sinceFor('7d', now)).toBe('2026-10-18T12:00:00.000Z');
    expect(sinceFor(undefined, now)).toBeUndefined();
    expect(sinceFor('bogus', now)).toBeUndefined();
  });
});
