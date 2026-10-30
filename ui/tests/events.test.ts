import { QueryClient } from '@tanstack/react-query';
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it, vi } from 'vitest';
import { applyEvent } from '../src/api/eventEffects';
import {
  EVENT_TYPES,
  parseEvent,
  type EventPayloads,
  type EventType,
  type StreamEvent,
} from '../src/api/events';
import { LiveStore } from '../src/api/live';
import { keys } from '../src/api/queryKeys';

const FIXTURES = join(__dirname, '..', '..', 'contracts', 'events');

function fixture<K extends EventType>(type: K): EventPayloads[K] {
  return JSON.parse(readFileSync(join(FIXTURES, `${type}.json`), 'utf8')) as EventPayloads[K];
}

/** The keys each hand-written type declares, from a sample object literal that must type-check. */
const DECLARED: { [K in EventType]: Record<keyof EventPayloads[K], true> } = {
  delta: { correlationId: true, content: true, threadId: true },
  snapshot: { correlationId: true, content: true, threadId: true },
  'message.created': { messageId: true, threadId: true, correlationId: true },
  'message.completed': { correlationId: true, messageId: true, content: true, threadId: true },
  error: { correlationId: true, code: true, message: true, threadId: true },
  'tool.requested': { correlationId: true, toolCallId: true, toolName: true, threadId: true },
  'approval.requested': {
    approvalId: true,
    correlationId: true,
    toolName: true,
    summary: true,
    risk: true,
    reason: true,
    expiresAt: true,
    threadId: true,
  },
  'approval.decided': {
    approvalId: true,
    correlationId: true,
    status: true,
    decidedBy: true,
    threadId: true,
    decidedByUser: true,
  },
  'thread.created': { threadId: true, agentId: true },
  'thread.updated': { threadId: true, title: true, archived: true, model: true },
  ready: { threadId: true },
  reset: { reason: true, threadId: true },
};

describe('event fixtures', () => {
  it('cover exactly the event types the UI handles', () => {
    const files = readdirSync(FIXTURES).map((f) => f.replace(/\.json$/, ''));
    expect([...files].sort()).toEqual([...EVENT_TYPES].sort());
  });

  it.each(EVENT_TYPES)('%s matches the hand-written type', (type) => {
    const data = fixture(type);
    expect(Object.keys(data).sort()).toEqual(Object.keys(DECLARED[type]).sort());
    expect(parseEvent(type, JSON.stringify(data))).toEqual(data);
  });
});

function harness() {
  const queryClient = new QueryClient();
  const live = new LiveStore((cb) => cb());
  const onTurnError = vi.fn();
  const invalidated: string[] = [];
  const spy = vi.spyOn(queryClient, 'invalidateQueries').mockImplementation(async (filters) => {
    invalidated.push(JSON.stringify(filters?.queryKey ?? 'ALL'));
  });
  const apply = <K extends EventType>(
    type: K,
    data: EventPayloads[K] = fixture(type),
    extra: Partial<StreamEvent> = {},
  ) => applyEvent({ type, data, id: '1', ...extra } as StreamEvent, { queryClient, live, onTurnError });
  return { queryClient, live, onTurnError, invalidated, spy, apply };
}

const k = (key: readonly unknown[]) => JSON.stringify(key);

describe('applyEvent (event → UI state)', () => {
  const t = fixture('delta').threadId;
  const corr = fixture('delta').correlationId;

  it('thread.created invalidates the list', () => {
    const h = harness();
    h.apply('thread.created');
    expect(h.invalidated).toEqual([k(keys.threads)]);
  });

  it('thread.updated patches list and detail caches', () => {
    const h = harness();
    h.queryClient.setQueryData(keys.threadList({ archived: false }), {
      pages: [{ threads: [{ id: t, title: 'old', archived: false }], nextCursor: null }],
      pageParams: [null],
    });
    h.queryClient.setQueryData(keys.thread(t), { thread: { id: t, title: 'old', archived: false } });
    h.apply('thread.updated');
    const list = h.queryClient.getQueryData<{ pages: { threads: { title: string }[] }[] }>(
      keys.threadList({ archived: false }),
    );
    expect(list?.pages[0]?.threads[0]?.title).toBe('Trip plan');
    expect(h.queryClient.getQueryData<{ thread: { title: string } }>(keys.thread(t))?.thread.title).toBe(
      'Trip plan',
    );
    // The model override travels too, so other tabs' pickers and status bars follow.
    expect(h.queryClient.getQueryData<{ thread: { model: string } }>(keys.thread(t))?.thread.model).toBe(
      'openai/gpt-4o-mini',
    );
  });

  it('thread.updated leaves non-paged thread queries alone and still invalidates them', () => {
    const h = harness();
    const recent = [...keys.threads, 'recent', 'agent-1'];
    h.queryClient.setQueryData(recent, { threads: [{ id: t, title: 'old' }], nextCursor: null });
    expect(() => h.apply('thread.updated')).not.toThrow();
    expect(h.invalidated).toContain(k(keys.threads));
  });

  it('message.created invalidates messages, list and detail', () => {
    const h = harness();
    h.apply('message.created');
    expect(h.invalidated).toEqual(
      expect.arrayContaining([k(keys.messages(t)), k(keys.threads), k(keys.thread(t))]),
    );
  });

  it('delta appends, snapshot replaces, tool.requested adds a live row — outside the query cache', () => {
    const h = harness();
    h.apply('delta', { correlationId: corr, content: 'Hel', threadId: t });
    h.apply('delta', { correlationId: corr, content: 'lo', threadId: t });
    expect(h.live.get(corr)?.text).toBe('Hello');
    h.apply('snapshot', { correlationId: corr, content: 'Hello wor', threadId: t });
    expect(h.live.get(corr)?.text).toBe('Hello wor');
    h.apply('tool.requested');
    h.apply('tool.requested');
    expect(h.live.get(corr)?.tools).toEqual([{ toolCallId: 'call_1', toolName: 'workspace_read' }]);
    expect(h.invalidated).toEqual([]);
  });

  it('message.completed clears the buffer and refetches messages, detail, list and trace', () => {
    const h = harness();
    h.apply('delta');
    h.apply('message.completed');
    expect(h.live.get(corr)).toBeUndefined();
    expect(h.invalidated).toEqual(
      expect.arrayContaining([k(keys.messages(t)), k(keys.thread(t)), k(keys.threads), k(keys.trace(t))]),
    );
  });

  it('error clears the buffer, refetches and raises a notice', () => {
    const h = harness();
    h.apply('delta');
    h.apply('error');
    expect(h.live.get(corr)).toBeUndefined();
    expect(h.onTurnError).toHaveBeenCalledWith({ threadId: t, correlationId: corr, code: 'provider_error' });
    expect(h.invalidated).toEqual(expect.arrayContaining([k(keys.messages(t)), k(keys.thread(t))]));
  });

  it.each(['approval.requested', 'approval.decided'] as const)(
    '%s invalidates approvals and the thread',
    (type) => {
      const h = harness();
      h.apply(type);
      expect(h.invalidated).toEqual(expect.arrayContaining([k(keys.approvals), k(keys.thread(t))]));
    },
  );

  it('ready only refetches after a reconnect', () => {
    const h = harness();
    h.apply('ready', undefined, { reconnected: false } as Partial<StreamEvent>);
    expect(h.invalidated).toEqual([]);
    h.apply('ready', undefined, { reconnected: true } as Partial<StreamEvent>);
    expect(h.invalidated).toEqual(
      expect.arrayContaining([k(keys.threads), k(keys.approvals), k(['thread']), k(['messages'])]),
    );
  });

  it('reset invalidates everything and clears live buffers', () => {
    const h = harness();
    h.apply('delta');
    h.apply('reset');
    expect(h.live.get(corr)).toBeUndefined();
    expect(h.invalidated).toContain('"ALL"');
  });

  it('handlers are idempotent', () => {
    const h = harness();
    h.apply('message.completed');
    h.apply('message.completed');
    h.apply('snapshot');
    h.apply('snapshot');
    expect(h.live.get(corr)?.text).toBe('Hello wor');
  });
});

describe('LiveStore', () => {
  it('reset cancels a pending frame and notifies at once', () => {
    let pending: (() => void) | null = null;
    const cancel = vi.fn(() => {
      pending = null;
    });
    const store = new LiveStore((callback) => {
      pending = callback;
      return cancel;
    });
    const listener = vi.fn();
    store.subscribe(listener);
    store.append('t', 'c', 'hi');
    expect(pending).not.toBeNull();
    expect(listener).not.toHaveBeenCalled();

    store.reset();

    expect(cancel).toHaveBeenCalledTimes(1);
    expect(listener).toHaveBeenCalledTimes(1);
    expect(store.forThread('t')).toEqual([]);
    // The next change schedules a fresh frame.
    store.append('t', 'c', 'again');
    expect(pending).not.toBeNull();
  });
});
