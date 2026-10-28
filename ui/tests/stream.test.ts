import { describe, expect, it, vi } from 'vitest';
import { BACKOFF_MS, EventStreamManager, HIDDEN_REOPEN_MS, type ConnectionState } from '../src/api/stream';
import type { StreamEvent } from '../src/api/events';
import { FakeEventSource } from './utils';

function setup(checkSession: () => Promise<boolean> = async () => true) {
  const sources: FakeEventSource[] = [];
  const windowTarget = new EventTarget();
  const doc = Object.assign(new EventTarget(), { visibilityState: 'visible' as DocumentVisibilityState });
  let now = 1_000_000;
  const timers: { fn: () => void; ms: number }[] = [];
  const onSignedOut = vi.fn();
  const manager = new EventStreamManager({
    createEventSource: (url) => {
      const s = new FakeEventSource(url);
      sources.push(s);
      return s;
    },
    checkSession,
    onSignedOut,
    window: windowTarget as unknown as Window,
    document: doc as unknown as Document,
    now: () => now,
    setTimeout: (fn, ms) => {
      timers.push({ fn, ms });
      return timers.length;
    },
    clearTimeout: () => {},
  });
  const events: StreamEvent[] = [];
  const states: ConnectionState[] = [];
  manager.subscribe((e) => events.push(e));
  manager.onStateChange((s) => states.push(s));
  return {
    manager,
    sources,
    events,
    states,
    timers,
    onSignedOut,
    windowTarget,
    doc,
    advance: (ms: number) => (now += ms),
    last: () => sources[sources.length - 1]!,
  };
}

const flush = () => new Promise((r) => setTimeout(r, 0));
const thread = 't-1';

describe('EventStreamManager', () => {
  it('opens one global stream and moves through connecting → open', () => {
    const t = setup();
    t.manager.start();
    t.manager.start();
    expect(t.sources).toHaveLength(1);
    expect(t.last().url).toBe('/api/events');
    expect(t.manager.connectionState).toBe('connecting');
    t.last().open();
    expect(t.manager.connectionState).toBe('open');
    t.last().emit('ready', { threadId: null }, 10);
    expect(t.manager.lastEventId).toBe('10');
  });

  it('records the id of every durable event and dedupes repeats', () => {
    const t = setup();
    t.manager.start();
    t.last().open();
    const created = { messageId: 'm', threadId: thread, correlationId: 'm' };
    t.last().emit('message.created', created, 11);
    t.last().emit('message.created', created, 11);
    t.last().emit('delta', { correlationId: 'm', content: 'a', threadId: thread });
    expect(t.events.map((e) => e.type)).toEqual(['message.created', 'delta']);
    expect(t.manager.lastEventId).toBe('11');
    // Malformed payloads are ignored.
    t.last().emit('delta', '{not json');
    t.last().emit('delta', { content: 'missing ids' });
    expect(t.events).toHaveLength(2);
  });

  it('a transient drop is left to the browser (no new EventSource)', () => {
    const t = setup();
    t.manager.start();
    t.last().open();
    t.last().drop();
    expect(t.manager.connectionState).toBe('reconnecting');
    expect(t.sources).toHaveLength(1);
    t.last().open();
    expect(t.manager.connectionState).toBe('open');
  });

  it('closed → session check → reopen with lastEventId and backoff 1s, 2s, 5s, 10s', async () => {
    const t = setup(async () => true);
    t.manager.start();
    t.last().open();
    t.last().emit('ready', { threadId: null }, 42);
    for (let i = 0; i < 5; i++) {
      t.last().fail();
      await flush();
      const timer = t.timers.at(-1)!;
      expect(timer.ms).toBe(BACKOFF_MS[Math.min(i, BACKOFF_MS.length - 1)]);
      timer.fn();
      expect(t.last().url).toBe('/api/events?lastEventId=42');
    }
    expect(t.sources).toHaveLength(6);
    // A successful open resets the backoff.
    t.last().open();
    t.last().fail();
    await flush();
    expect(t.timers.at(-1)!.ms).toBe(1000);
  });

  it('closed and signed out → stops and reports signed_out', async () => {
    const t = setup(async () => false);
    t.manager.start();
    t.last().fail();
    await flush();
    expect(t.manager.connectionState).toBe('signed_out');
    expect(t.onSignedOut).toHaveBeenCalledTimes(1);
    expect(t.timers).toHaveLength(0);
  });

  it('reopens after being hidden for more than 10 s, and on online', () => {
    const t = setup();
    t.manager.start();
    t.last().open();
    t.last().emit('ready', { threadId: null }, 7);

    t.doc.visibilityState = 'hidden';
    t.doc.dispatchEvent(new Event('visibilitychange'));
    t.advance(5_000);
    t.doc.visibilityState = 'visible';
    t.doc.dispatchEvent(new Event('visibilitychange'));
    expect(t.sources).toHaveLength(1);

    t.doc.visibilityState = 'hidden';
    t.doc.dispatchEvent(new Event('visibilitychange'));
    t.advance(HIDDEN_REOPEN_MS + 1);
    t.doc.visibilityState = 'visible';
    t.doc.dispatchEvent(new Event('visibilitychange'));
    expect(t.sources).toHaveLength(2);
    expect(t.sources[0]!.closed).toBe(true);
    expect(t.last().url).toBe('/api/events?lastEventId=7');

    t.windowTarget.dispatchEvent(new Event('online'));
    expect(t.sources).toHaveLength(3);
  });

  it('marks ready after a reconnect, and reset forgets the last id', () => {
    const t = setup();
    t.manager.start();
    t.last().open();
    t.last().emit('ready', { threadId: null }, 5);
    t.manager.reopen();
    t.last().open();
    t.last().emit('message.created', { messageId: 'm', threadId: thread, correlationId: 'm' }, 6);
    t.last().emit('ready', { threadId: null }, 6);
    const readies = t.events.filter((e) => e.type === 'ready');
    expect(readies.map((e) => e.reconnected)).toEqual([false, true]);

    t.last().emit('reset', { reason: 'history_pruned', threadId: null });
    expect(t.manager.lastEventId).toBeNull();
    t.manager.reopen();
    expect(t.last().url).toBe('/api/events');
  });

  it('a replay after reopen does not duplicate events', () => {
    const t = setup();
    t.manager.start();
    t.last().open();
    const completed = { correlationId: 'm', messageId: 'r', content: 'hi', threadId: thread };
    t.last().emit('message.completed', completed, 20);
    t.manager.reopen();
    t.last().open();
    t.last().emit('message.completed', completed, 20);
    expect(t.events.filter((e) => e.type === 'message.completed')).toHaveLength(1);
  });

  it('ignores events from a closed source and stop() clears state', () => {
    const t = setup();
    t.manager.start();
    const first = t.last();
    t.manager.reopen();
    first.emit('delta', { correlationId: 'm', content: 'late', threadId: thread });
    expect(t.events).toHaveLength(0);
    t.manager.stop();
    expect(t.last().closed).toBe(true);
    expect(t.manager.lastEventId).toBeNull();
    expect(t.states.at(-1)).toBe('idle');
  });
});
