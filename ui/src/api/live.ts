/**
 * Per-turn streaming state fed by the event stream: the text streamed so far and live tool rows,
 * keyed by correlation id. Kept out of the query cache on purpose (3.3 spec section 8.3).
 * Listeners are notified at most once per animation frame, so a burst of deltas renders once.
 */
export interface LiveTool {
  toolCallId: string;
  toolName: string;
}

export interface LiveTurn {
  threadId: string;
  correlationId: string;
  text: string;
  tools: LiveTool[];
}

/** Runs `callback` later; may return a function that cancels it. */
type Schedule = (callback: () => void) => (() => void) | void;

const defaultSchedule: Schedule = (callback) => {
  if (typeof requestAnimationFrame === 'function') {
    const frame = requestAnimationFrame(() => callback());
    return () => cancelAnimationFrame(frame);
  }
  const timer = setTimeout(callback, 16);
  return () => clearTimeout(timer);
};

export class LiveStore {
  private readonly turns = new Map<string, LiveTurn>();
  private readonly listeners = new Set<() => void>();
  private version = 0;
  private scheduled = false;
  private cancelScheduled: (() => void) | null = null;
  private readonly cache = new Map<string, { version: number; turns: LiveTurn[] }>();

  constructor(private readonly schedule: Schedule = defaultSchedule) {}

  subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };

  getVersion = () => this.version;

  /** The thread's in-flight turns (stable between changes, for useSyncExternalStore). */
  forThread(threadId: string): LiveTurn[] {
    const cached = this.cache.get(threadId);
    if (cached && cached.version === this.version) return cached.turns;
    const turns = [...this.turns.values()].filter((t) => t.threadId === threadId);
    this.cache.set(threadId, { version: this.version, turns });
    return turns;
  }

  get(correlationId: string): LiveTurn | undefined {
    return this.turns.get(correlationId);
  }

  append(threadId: string, correlationId: string, text: string) {
    const turn = this.ensure(threadId, correlationId);
    this.turns.set(correlationId, { ...turn, text: turn.text + text });
    this.changed();
  }

  /** A snapshot is the whole text so far: it replaces whatever the buffer had. */
  replace(threadId: string, correlationId: string, text: string) {
    const turn = this.ensure(threadId, correlationId);
    this.turns.set(correlationId, { ...turn, text });
    this.changed();
  }

  addTool(threadId: string, correlationId: string, tool: LiveTool) {
    const turn = this.ensure(threadId, correlationId);
    if (turn.tools.some((t) => t.toolCallId === tool.toolCallId)) return;
    this.turns.set(correlationId, { ...turn, tools: [...turn.tools, tool] });
    this.changed();
  }

  clear(correlationId: string) {
    if (this.turns.delete(correlationId)) this.changed();
  }

  clearAll() {
    if (this.turns.size === 0) return;
    this.turns.clear();
    this.changed();
  }

  /**
   * Sign-out: drops every turn and notifies now, cancelling any pending frame so nothing from the
   * previous session fires afterwards.
   */
  reset() {
    this.cancelScheduled?.();
    this.cancelScheduled = null;
    this.scheduled = false;
    this.turns.clear();
    this.cache.clear();
    this.version++;
    for (const listener of [...this.listeners]) listener();
  }

  private ensure(threadId: string, correlationId: string): LiveTurn {
    return this.turns.get(correlationId) ?? { threadId, correlationId, text: '', tools: [] };
  }

  private changed() {
    this.version++;
    if (this.scheduled) return;
    this.scheduled = true;
    this.cancelScheduled =
      this.schedule(() => {
        this.scheduled = false;
        this.cancelScheduled = null;
        for (const listener of [...this.listeners]) listener();
      }) ?? null;
  }
}
