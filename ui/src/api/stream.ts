import { EVENT_TYPES, parseEvent, type EventType, type StreamEvent } from './events';

/** The subset of `EventSource` the manager uses, so tests can supply a fake. */
export interface EventSourceLike {
  readonly readyState: number;
  onopen: ((ev: Event) => unknown) | null;
  onerror: ((ev: Event) => unknown) | null;
  addEventListener(type: string, listener: (ev: MessageEvent) => void): void;
  close(): void;
}

export type ConnectionState = 'idle' | 'connecting' | 'open' | 'reconnecting' | 'signed_out';

export interface StreamManagerDeps {
  createEventSource: (url: string) => EventSourceLike;
  /** True while the user is still signed in. */
  checkSession: () => Promise<boolean>;
  onSignedOut: () => void;
  window?: Pick<Window, 'addEventListener' | 'removeEventListener'>;
  document?: Pick<Document, 'addEventListener' | 'removeEventListener' | 'visibilityState'>;
  now?: () => number;
  setTimeout?: (fn: () => void, ms: number) => unknown;
  clearTimeout?: (handle: unknown) => void;
}

const CLOSED = 2;
/** Backoff for reopening after the browser gives up (a non-200 response). */
export const BACKOFF_MS = [1000, 2000, 5000, 10000];
/** A tab hidden longer than this is reopened when shown (cures half-open connections on phones). */
export const HIDDEN_REOPEN_MS = 10_000;
const MAX_REMEMBERED_IDS = 500;

/**
 * Events sent without an `id:` line. Browsers keep the connection's last event id and report it
 * on every later event, so for these `MessageEvent.lastEventId` is stale and must be ignored.
 */
const EPHEMERAL = new Set<EventType>(['delta', 'snapshot', 'reset']);

/**
 * Owns the tab's single connection to the global `/api/events` stream (3.3 spec section 8.2).
 * Over plain HTTP/1.1 browsers allow six connections per host, so there is never more than one.
 *
 * - The browser's automatic reconnect handles transient drops and re-sends `Last-Event-ID`.
 * - If the `EventSource` ends up CLOSED (a non-200 response), the session is checked: signed out
 *   ends the stream; otherwise a new `EventSource` is opened with `?lastEventId=` and backoff.
 * - Durable events are de-duplicated by id, so a replay after a reopen is harmless.
 */
export class EventStreamManager {
  private source: EventSourceLike | null = null;
  private state: ConnectionState = 'idle';
  private lastId: string | null = null;
  private readonly seenIds = new Set<string>();
  private readonly listeners = new Set<(event: StreamEvent) => void>();
  private readonly stateListeners = new Set<(state: ConnectionState) => void>();
  private attempt = 0;
  private timer: unknown = null;
  private hiddenAt: number | null = null;
  private everOpened = false;
  private stopped = true;
  /**
   * Bumped by every open and stop. Async work (a session check after a CLOSED source, a backoff
   * timer) remembers the generation it started in and does nothing if it has changed, so a stale
   * result can never act on a newer connection (e.g. stop the stream after a quick re-login).
   */
  private generation = 0;
  private readonly deps: Required<Omit<StreamManagerDeps, 'window' | 'document'>> &
    Pick<StreamManagerDeps, 'window' | 'document'>;

  constructor(deps: StreamManagerDeps) {
    this.deps = {
      now: () => Date.now(),
      setTimeout: (fn, ms) => globalThis.setTimeout(fn, ms),
      clearTimeout: (h) => globalThis.clearTimeout(h as ReturnType<typeof setTimeout>),
      ...deps,
    };
  }

  get connectionState() {
    return this.state;
  }

  get lastEventId() {
    return this.lastId;
  }

  subscribe(listener: (event: StreamEvent) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  onStateChange(listener: (state: ConnectionState) => void): () => void {
    this.stateListeners.add(listener);
    return () => this.stateListeners.delete(listener);
  }

  /** Opens the stream (after authentication). Safe to call again. */
  start() {
    if (!this.stopped) return;
    this.stopped = false;
    this.deps.window?.addEventListener('online', this.handleOnline);
    this.deps.document?.addEventListener('visibilitychange', this.handleVisibility);
    this.open();
  }

  /** Closes everything (sign-out). */
  stop(state: ConnectionState = 'idle') {
    this.stopped = true;
    this.generation++;
    this.clearTimer();
    this.source?.close();
    this.source = null;
    this.deps.window?.removeEventListener('online', this.handleOnline);
    this.deps.document?.removeEventListener('visibilitychange', this.handleVisibility);
    this.lastId = null;
    this.seenIds.clear();
    this.everOpened = false;
    this.attempt = 0;
    this.setState(state);
  }

  /** Closes and reopens from the last durable id (visibility, online, backoff). */
  reopen() {
    if (this.stopped) return;
    this.clearTimer();
    this.source?.close();
    this.source = null;
    this.open();
  }

  private open() {
    const url = this.lastId ? `/api/events?lastEventId=${encodeURIComponent(this.lastId)}` : '/api/events';
    this.setState(this.everOpened ? 'reconnecting' : 'connecting');
    this.generation++;
    const source = this.deps.createEventSource(url);
    this.source = source;
    source.onopen = () => {
      if (this.source !== source) return;
      this.attempt = 0;
      this.setState('open');
    };
    source.onerror = () => {
      if (this.source !== source) return;
      if (source.readyState === CLOSED) void this.handleClosed(source);
      else this.setState('reconnecting'); // the browser is retrying by itself
    };
    for (const type of EVENT_TYPES) source.addEventListener(type, (ev) => this.dispatch(source, type, ev));
  }

  private async handleClosed(source: EventSourceLike) {
    source.close();
    if (this.source === source) this.source = null;
    this.setState('reconnecting');
    const generation = this.generation;
    const signedIn = await this.deps.checkSession();
    // Stopped, reopened or restarted meanwhile: this answer is about a connection that is gone.
    if (this.stopped || generation !== this.generation) return;
    if (!signedIn) {
      this.stop('signed_out');
      this.deps.onSignedOut();
      return;
    }
    const delay = BACKOFF_MS[Math.min(this.attempt, BACKOFF_MS.length - 1)]!;
    this.attempt++;
    this.clearTimer();
    this.timer = this.deps.setTimeout(() => {
      this.timer = null;
      if (!this.stopped && generation === this.generation && !this.source) this.open();
    }, delay);
  }

  private dispatch(source: EventSourceLike, type: EventType, ev: MessageEvent) {
    if (this.source !== source) return;
    const id = !EPHEMERAL.has(type) && ev.lastEventId ? ev.lastEventId : null;
    // Durable events (and ready) carry an id; replays after a reopen are dropped by id.
    if (id !== null && type !== 'ready') {
      if (this.seenIds.has(id)) return;
      this.remember(id);
    }
    if (id !== null) this.lastId = id;

    const data = parseEvent(type, typeof ev.data === 'string' ? ev.data : '');
    if (data === null) return;

    let reconnected = false;
    if (type === 'ready') {
      reconnected = this.everOpened;
      this.everOpened = true;
      if (this.state !== 'open') this.setState('open');
    }
    if (type === 'reset') {
      this.lastId = null;
      this.seenIds.clear();
    }
    const event = { type, data, id, ...(type === 'ready' ? { reconnected } : {}) } as StreamEvent;
    for (const listener of [...this.listeners]) listener(event);
  }

  private remember(id: string) {
    this.seenIds.add(id);
    if (this.seenIds.size > MAX_REMEMBERED_IDS) {
      const oldest = this.seenIds.values().next().value;
      if (oldest !== undefined) this.seenIds.delete(oldest);
    }
  }

  private readonly handleOnline = () => this.reopen();

  private readonly handleVisibility = () => {
    const doc = this.deps.document;
    if (!doc) return;
    if (doc.visibilityState === 'hidden') {
      this.hiddenAt = this.deps.now();
      return;
    }
    const hiddenFor = this.hiddenAt === null ? 0 : this.deps.now() - this.hiddenAt;
    this.hiddenAt = null;
    if (hiddenFor > HIDDEN_REOPEN_MS) this.reopen();
  };

  private clearTimer() {
    if (this.timer !== null) {
      this.deps.clearTimeout(this.timer);
      this.timer = null;
    }
  }

  private setState(state: ConnectionState) {
    if (this.state === state) return;
    this.state = state;
    for (const listener of [...this.stateListeners]) listener(state);
  }
}
