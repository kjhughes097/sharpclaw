import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { axe } from 'jest-axe';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it } from 'vitest';
import type { components } from '../src/api/schema';
import { clearDraftsForTests } from '../src/features/chat/Composer';
import { apiError, fixtures, server } from './msw';
import { renderApp } from './utils';

type S = components['schemas'];
const T = 'thread-1';

function detail(
  over: Partial<S['ThreadDetailResponse']> = {},
  agent: Partial<S['AgentDto']> = {},
): S['ThreadDetailResponse'] {
  return {
    thread: {
      id: T,
      agentId: 'agent-1',
      title: 'Trip plan',
      createdAt: '2026-10-25T10:00:00Z',
      archived: false,
      lastMessageAt: '2026-10-25T11:00:00Z',
      context: { droppedTurns: 0, estimatedTokens: 5100, windowTokens: 32000 },
    },
    agent: fixtures.agent(agent),
    inFlight: null,
    queued: 0,
    pendingApprovals: [],
    ...over,
  };
}

function useThread(
  opts: { detail?: S['ThreadDetailResponse']; messages?: S['MessageDto'][]; hasMoreBefore?: boolean } = {},
) {
  const state = { messages: opts.messages ?? [], detail: opts.detail ?? detail(), posts: [] as string[] };
  server.use(
    http.get('*/api/threads/:id', () => HttpResponse.json(state.detail)),
    http.get('*/api/threads/:id/messages', ({ request }) => {
      const url = new URL(request.url);
      expect(url.searchParams.get('includeTools')).toBe('true');
      const before = url.searchParams.get('beforeSeq');
      const rows = before ? state.messages.filter((r) => (r.seq ?? 0) < Number(before)) : state.messages;
      return HttpResponse.json({
        messages: rows,
        hasMoreBefore: before ? false : (opts.hasMoreBefore ?? false),
      });
    }),
    http.get('*/api/threads/:id/trace', () => HttpResponse.json({ turns: [] })),
    http.get('*/api/threads', () => HttpResponse.json({ threads: [fixtures.thread()], nextCursor: null })),
    http.post('*/api/threads/:id/messages', async ({ request }) => {
      const body = (await request.json()) as { content: string; unattended: boolean };
      expect(body.unattended).toBe(false);
      state.posts.push(body.content);
      return HttpResponse.json(
        {
          message: {
            id: `posted-${state.posts.length}`,
            status: 'pending',
            createdAt: '2026-10-25T12:00:00Z',
          },
        },
        { status: 202 },
      );
    }),
  );
  return state;
}

const user1 = fixtures.message({ id: 'u1', seq: 1, content: 'Plan my **trip**' });
const reply1 = fixtures.message({
  id: 'r1',
  seq: 2,
  role: 'assistant',
  correlationId: 'u1',
  content: 'Here is **the plan**',
});

beforeEach(() => clearDraftsForTests());

describe('thread view', () => {
  it('renders user text plainly and assistant text as markdown', async () => {
    useThread({ messages: [user1, reply1] });
    const { container } = renderApp(`/chats/${T}`);
    expect(await screen.findByText('Plan my **trip**')).toBeInTheDocument();
    expect(within(container).getByText('the plan').tagName).toBe('STRONG');
    expect(screen.getByText('~5.1k / 32k tokens')).toBeInTheDocument();
  });

  it('shows the sent message at once and dedupes it against message.created', async () => {
    const state = useThread({ messages: [user1, reply1] });
    const { user, services } = renderApp(`/chats/${T}`);
    const box = await screen.findByLabelText('Message');
    await user.type(box, 'Book the hotel');
    await user.click(screen.getByRole('button', { name: 'Send' }));
    expect(await screen.findByText('Book the hotel')).toBeInTheDocument();
    expect(state.posts).toEqual(['Book the hotel']);
    expect(box).toHaveValue('');

    // The real row arrives with the same id.
    state.messages = [
      ...state.messages,
      fixtures.message({ id: 'posted-1', seq: 3, content: 'Book the hotel', status: 'pending' }),
    ];
    act(() =>
      services
        .source()
        .emit('message.created', { messageId: 'posted-1', threadId: T, correlationId: 'posted-1' }, 9),
    );
    await waitFor(() => expect(screen.getAllByText('Book the hotel')).toHaveLength(1));
    expect(screen.getByText('Queued')).toBeInTheDocument();
  });

  it('a failed message shows the reason and Resend posts the same content', async () => {
    const state = useThread({
      messages: [
        fixtures.message({
          id: 'u1',
          seq: 1,
          content: 'try this',
          status: 'failed',
          failReason: 'provider_timeout',
        }),
      ],
    });
    const { user } = renderApp(`/chats/${T}`);
    expect(await screen.findByText('The model provider timed out. Try again.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Resend' }));
    await waitFor(() => expect(state.posts).toEqual(['try this']));
  });

  it('streams deltas into a bubble, then the persisted reply replaces it', async () => {
    const state = useThread({
      messages: [fixtures.message({ id: 'u1', seq: 1, content: 'hi', status: 'claimed' })],
      detail: detail({ inFlight: { correlationId: 'u1', claimedAt: '2026-10-25T11:59:58Z' } }),
    });
    const { services } = renderApp(`/chats/${T}`);
    expect(await screen.findByText(/Thinking…/)).toBeInTheDocument();
    act(() => {
      services.source().open();
      services.source().emit('delta', { correlationId: 'u1', content: 'Hel', threadId: T });
      services.source().emit('delta', { correlationId: 'u1', content: 'lo there', threadId: T });
    });
    await waitFor(() => expect(screen.getByText('Hello there')).toBeInTheDocument());
    expect(screen.getByText('Assistant is replying')).toBeInTheDocument();

    state.messages = [
      fixtures.message({ id: 'u1', seq: 1, content: 'hi', status: 'done' }),
      fixtures.message({
        id: 'r1',
        seq: 2,
        role: 'assistant',
        correlationId: 'u1',
        content: 'Hello there, friend',
      }),
    ];
    state.detail = detail();
    act(() =>
      services
        .source()
        .emit(
          'message.completed',
          { correlationId: 'u1', messageId: 'r1', content: 'Hello there, friend', threadId: T },
          12,
        ),
    );
    expect(await screen.findByText('Hello there, friend')).toBeInTheDocument();
    expect(screen.queryByText('Hello there')).toBeNull();
    expect(screen.getByText('Reply complete')).toBeInTheDocument();
  });

  it('a snapshot on reconnect replaces the partial text without duplication', async () => {
    useThread({
      messages: [fixtures.message({ id: 'u1', seq: 1, content: 'hi', status: 'claimed' })],
      detail: detail({ inFlight: { correlationId: 'u1', claimedAt: '2026-10-25T11:59:58Z' } }),
    });
    const { services } = renderApp(`/chats/${T}`);
    await screen.findByText(/Thinking…/);
    act(() => {
      services.source().emit('delta', { correlationId: 'u1', content: 'Hello', threadId: T });
      services.source().emit('snapshot', { correlationId: 'u1', content: 'Hello wor', threadId: T });
      services.source().emit('delta', { correlationId: 'u1', content: 'ld', threadId: T });
    });
    await waitFor(() => expect(screen.getByText('Hello world')).toBeInTheDocument());
  });

  it('shows the disabled, budget and agent-away banners', async () => {
    useThread({
      detail: detail(
        { queued: 1 },
        { enabled: false, remainingUsd: 0, todaySpendUsd: 1, lastSeenAt: '2026-10-25T11:00:00Z' },
      ),
    });
    renderApp(`/chats/${T}`);
    expect(
      await screen.findByText('This agent is disabled. Messages will queue until it is enabled.'),
    ).toBeInTheDocument();
    expect(screen.getByText(/Daily budget used up\. Resets at/)).toBeInTheDocument();
    expect(screen.getByText("The agent hasn't connected recently.")).toBeInTheDocument();
  });

  it('shows the dropped-turns banner from the thread context', async () => {
    const d = detail();
    d.thread.context = { droppedTurns: 4, estimatedTokens: 2900, windowTokens: 3000 };
    useThread({ detail: d });
    renderApp(`/chats/${T}`);
    expect(
      await screen.findByText('The model no longer sees the oldest 4 turns of this conversation.'),
    ).toBeInTheDocument();
  });

  it('keeps drafts per thread in memory only', async () => {
    useThread();
    server.use(
      http.get('*/api/threads', () =>
        HttpResponse.json({
          threads: [fixtures.thread(), fixtures.thread({ id: 'thread-2', title: 'Other' })],
          nextCursor: null,
        }),
      ),
    );
    const { user } = renderApp(`/chats/${T}`);
    await user.type(await screen.findByLabelText('Message'), 'half-written');
    await user.click(await screen.findByRole('link', { name: /Other/ }));
    await waitFor(() => expect(screen.getByLabelText('Message')).toHaveValue(''));
    await user.click(screen.getByRole('link', { name: /Trip plan/ }));
    await waitFor(() => expect(screen.getByLabelText('Message')).toHaveValue('half-written'));
    expect(JSON.stringify({ ...localStorage })).not.toContain('half-written');
    expect(JSON.stringify({ ...sessionStorage })).not.toContain('half-written');
  });

  it('Enter sends with a fine pointer; Shift+Enter adds a newline', async () => {
    const state = useThread();
    const original = window.matchMedia;
    window.matchMedia = ((q: string) => ({
      ...original(q),
      matches: q === '(pointer: fine)',
    })) as typeof window.matchMedia;
    try {
      const { user } = renderApp(`/chats/${T}`);
      const box = await screen.findByLabelText('Message');
      await user.type(box, 'line one{Shift>}{Enter}{/Shift}line two');
      expect(box).toHaveValue('line one\nline two');
      await user.type(box, '{Enter}');
      await waitFor(() => expect(state.posts).toEqual(['line one\nline two']));
    } finally {
      window.matchMedia = original;
    }
  });

  it('on touch devices Enter is a newline and Send sends', async () => {
    const state = useThread();
    const { user } = renderApp(`/chats/${T}`);
    const box = await screen.findByLabelText('Message');
    await user.type(box, 'a{Enter}b');
    expect(box).toHaveValue('a\nb');
    expect(state.posts).toEqual([]);
    await user.type(box, '{Control>}{Enter}{/Control}');
    await waitFor(() => expect(state.posts).toEqual(['a\nb']));
  });

  it('disables Send when empty or over the limit and shows a counter near it', async () => {
    useThread();
    renderApp(`/chats/${T}`);
    const box = await screen.findByLabelText('Message');
    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
    fireEvent.change(box, { target: { value: 'x'.repeat(32001) } });
    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
    expect(screen.getByText('32,001 / 32,000 characters')).toBeInTheDocument();
  });

  it('a send error keeps the draft and explains', async () => {
    useThread();
    server.use(
      http.post('*/api/threads/:id/messages', () =>
        apiError(400, 'validation_failed', 'content exceeds 32000 characters.'),
      ),
    );
    const { user } = renderApp(`/chats/${T}`);
    const box = await screen.findByLabelText('Message');
    await user.type(box, 'hello');
    await user.click(screen.getByRole('button', { name: 'Send' }));
    expect(await screen.findByText('content exceeds 32000 characters.')).toBeInTheDocument();
    expect(box).toHaveValue('hello');
  });
});

/** jsdom has no layout: give the scroll container controllable metrics. */
function scrollMetrics(el: HTMLElement, metrics: { scrollHeight: number; clientHeight: number }) {
  let height = metrics.scrollHeight;
  Object.defineProperty(el, 'scrollHeight', { configurable: true, get: () => height });
  Object.defineProperty(el, 'clientHeight', { configurable: true, get: () => metrics.clientHeight });
  return { setHeight: (h: number) => (height = h) };
}

describe('scrolling', () => {
  it('shows "Jump to latest" when scrolled up and new content arrives', async () => {
    useThread({
      messages: [fixtures.message({ id: 'u1', seq: 1, content: 'hi', status: 'claimed' })],
      detail: detail({ inFlight: { correlationId: 'u1', claimedAt: '2026-10-25T11:59:58Z' } }),
    });
    const { services, user } = renderApp(`/chats/${T}`);
    await screen.findByText(/Thinking…/);
    const scroller = screen.getByTestId('message-scroll');
    const m = scrollMetrics(scroller, { scrollHeight: 2000, clientHeight: 500 });
    scroller.scrollTop = 100;
    fireEvent.scroll(scroller);
    m.setHeight(2400);
    act(() => services.source().emit('delta', { correlationId: 'u1', content: 'more text', threadId: T }));
    const pill = await screen.findByRole('button', { name: /Jump to latest/ });
    expect(scroller.scrollTop).toBe(100);
    await user.click(pill);
    expect(scroller.scrollTop).toBe(2400);
    expect(screen.queryByRole('button', { name: /Jump to latest/ })).toBeNull();
  });

  it('"Load earlier messages" keeps the scroll position', async () => {
    const older = Array.from({ length: 3 }, (_, i) =>
      fixtures.message({ id: `old-${i}`, seq: i + 1, content: `old ${i}` }),
    );
    const newer = [fixtures.message({ id: 'new-1', seq: 10, content: 'new one' })];
    const state = useThread({ messages: newer, hasMoreBefore: true });
    server.use(
      http.get('*/api/threads/:id/messages', ({ request }) => {
        const before = new URL(request.url).searchParams.get('beforeSeq');
        return before
          ? HttpResponse.json({ messages: older, hasMoreBefore: false })
          : HttpResponse.json({ messages: state.messages, hasMoreBefore: true });
      }),
    );
    const { user } = renderApp(`/chats/${T}`);
    await screen.findByText('new one');
    const scroller = screen.getByTestId('message-scroll');
    // 200 px per rendered turn plus 400 px of chrome.
    Object.defineProperty(scroller, 'scrollHeight', {
      configurable: true,
      get: () => 400 + 200 * scroller.querySelectorAll('li[data-turn]').length,
    });
    Object.defineProperty(scroller, 'clientHeight', { configurable: true, get: () => 300 });
    scroller.scrollTop = 0;
    fireEvent.scroll(scroller);
    await user.click(screen.getByRole('button', { name: 'Load earlier messages' }));
    expect(await screen.findByText('old 0')).toBeInTheDocument();
    // The three prepended turns (600 px) are compensated, so the same content stays in view.
    expect(scroller.scrollTop).toBe(600);
    expect(screen.queryByRole('button', { name: 'Load earlier messages' })).toBeNull();
  });
});

describe('tool activity', () => {
  const toolRows = [
    fixtures.message({ id: 'u1', seq: 1, content: 'read notes' }),
    fixtures.message({
      id: 'tr',
      seq: 2,
      role: 'assistant',
      kind: 'tool_request',
      correlationId: 'u1',
      content: '',
      toolCalls: [{ id: 'call_1', name: 'workspace_read', arguments: '{"path":"<b>notes</b>.md"}' }],
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
      truncated: true,
      content: '**not markdown** <script>x</script>',
    }),
    fixtures.message({ id: 'r1', seq: 4, role: 'assistant', correlationId: 'u1', content: 'Done' }),
  ];

  it('collapses to a summary and expands to plain-text cards with badges', async () => {
    useThread({ messages: toolRows });
    const { user, container } = renderApp(`/chats/${T}`);
    await user.click(await screen.findByRole('button', { name: /Used 1 tool/ }));
    expect(screen.getByText(/untrusted · not replayed to the model on later turns/)).toBeInTheDocument();
    expect(screen.getByText('truncated')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /workspace_read/ }));
    expect(screen.getByText(/\*\*not markdown\*\* <script>x<\/script>/)).toBeInTheDocument();
    expect(container.querySelector('script')).toBeNull();
    expect(container.querySelector('b')).toBeNull();
    // No trace linkage: no risk or decision badges.
    expect(screen.queryByText(/risk:/)).toBeNull();
  });

  it('enriches cards only when the trace links the tool-call id', async () => {
    useThread({ messages: toolRows });
    server.use(
      http.get('*/api/threads/:id/trace', () =>
        HttpResponse.json({
          turns: [
            {
              correlationId: 'u1',
              userMessageId: 'u1',
              status: 'done',
              failReason: null,
              createdAt: '2026-10-25T11:00:00Z',
              claimedAt: null,
              completedAt: null,
              llmCalls: [],
              approvals: [],
              totals: { costUsd: 0, promptTokens: 0, completionTokens: 0, llmCalls: 0, toolCalls: 1 },
              toolCalls: [
                {
                  id: 'tc',
                  toolCallId: 'call_1',
                  toolName: 'workspace_read',
                  risk: 'low',
                  decision: 'allowed',
                  outcome: 'succeeded',
                  approvalId: null,
                  durationMs: 12,
                  createdAt: '2026-10-25T11:00:00Z',
                  reportedAt: '2026-10-25T11:00:01Z',
                },
              ],
            },
          ],
        }),
      ),
    );
    const { user } = renderApp(`/chats/${T}`);
    await user.click(await screen.findByRole('button', { name: /Used 1 tool/ }));
    expect(await screen.findByText('risk: low')).toBeInTheDocument();
    expect(screen.getByText('decision: allowed')).toBeInTheDocument();
    expect(screen.getByText('12 ms')).toBeInTheDocument();
  });

  it('shows live "running…" rows from tool.requested', async () => {
    useThread({
      messages: [fixtures.message({ id: 'u1', seq: 1, content: 'go', status: 'claimed' })],
      detail: detail({ inFlight: { correlationId: 'u1', claimedAt: '2026-10-25T11:59:58Z' } }),
    });
    const { services } = renderApp(`/chats/${T}`);
    await screen.findByText(/Thinking…/);
    act(() =>
      services
        .source()
        .emit(
          'tool.requested',
          { correlationId: 'u1', toolCallId: 'call_9', toolName: 'workspace_list', threadId: T },
          3,
        ),
    );
    expect(await screen.findByText('workspace_list')).toBeInTheDocument();
    expect(screen.getByText(/running…/)).toBeInTheDocument();
  });
});

describe('thread list', () => {
  it('shows rows with badges and opens a new chat with an enabled agent', async () => {
    server.use(
      http.get('*/api/threads', () =>
        HttpResponse.json({
          threads: [fixtures.thread({ inFlight: true, pendingApprovals: 2, queued: 1 })],
          nextCursor: null,
        }),
      ),
      http.get('*/api/agents', () =>
        HttpResponse.json({
          agents: [fixtures.agent(), fixtures.agent({ id: 'agent-2', name: 'off', enabled: false })],
        }),
      ),
      http.post('*/api/threads', async ({ request }) => {
        expect(await request.json()).toEqual({ agentId: 'agent-1', title: null });
        return HttpResponse.json(
          { thread: { id: 'thread-new', agentId: 'agent-1', title: null, createdAt: '' } },
          { status: 201 },
        );
      }),
      http.get('*/api/threads/thread-new', () => HttpResponse.json(detail())),
      http.get('*/api/threads/thread-new/messages', () =>
        HttpResponse.json({ messages: [], hasMoreBefore: false }),
      ),
    );
    const { user } = renderApp('/chats');
    expect(await screen.findByText('2 approvals')).toBeInTheDocument();
    expect(screen.getByText('1 queued')).toBeInTheDocument();
    expect(screen.getByText('replying')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'New chat' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).queryByText('off')).toBeNull();
    await user.click(within(dialog).getByRole('button', { name: /assistant/ }));
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/chats/thread-new'));
  });

  it('pages with Load more', async () => {
    server.use(
      http.get('*/api/threads', ({ request }) => {
        const cursor = new URL(request.url).searchParams.get('cursor');
        return cursor === 'c2'
          ? HttpResponse.json({
              threads: [fixtures.thread({ id: 'thread-2', title: 'Second page' })],
              nextCursor: null,
            })
          : HttpResponse.json({ threads: [fixtures.thread()], nextCursor: 'c2' });
      }),
    );
    const { user } = renderApp('/chats');
    await user.click(await screen.findByRole('button', { name: 'Load more' }));
    expect(await screen.findByText('Second page')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull();
  });

  it('has no accessibility violations', async () => {
    useThread({ messages: [user1, reply1] });
    const { container } = renderApp(`/chats/${T}`);
    await screen.findByText('Plan my **trip**');
    expect(await axe(container)).toHaveNoViolations();
  });
});
