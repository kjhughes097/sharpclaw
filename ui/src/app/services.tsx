import { createContext, useContext, useMemo, useSyncExternalStore, type ReactNode } from 'react';
import { notifyUnauthorized, refreshSession } from '../api/client';
import type { ThreadNotice } from '../api/eventEffects';
import { LiveStore, type LiveTurn } from '../api/live';
import { EventStreamManager, type ConnectionState, type EventSourceLike } from '../api/stream';

/** Turn failures reported by `error` events, shown by the thread being viewed. */
export class NoticeStore {
  private notices: ThreadNotice[] = [];
  private readonly listeners = new Set<() => void>();

  subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };
  get = () => this.notices;
  push(notice: ThreadNotice) {
    if (this.notices.some((n) => n.correlationId === notice.correlationId)) return;
    this.notices = [...this.notices, notice];
    this.listeners.forEach((l) => l());
  }
  dismiss(correlationId: string) {
    this.notices = this.notices.filter((n) => n.correlationId !== correlationId);
    this.listeners.forEach((l) => l());
  }
  clear() {
    this.notices = [];
    this.listeners.forEach((l) => l());
  }
}

export interface Services {
  stream: EventStreamManager;
  live: LiveStore;
  notices: NoticeStore;
}

const ServicesContext = createContext<Services | null>(null);

/** The real services: one EventSource per tab against the same-origin gateway. */
export function createServices(): Services {
  const stream = new EventStreamManager({
    createEventSource: (url) => new EventSource(url) as unknown as EventSourceLike,
    checkSession: refreshSession,
    // The auth provider reacts to a signed-out stream through notifyUnauthorized.
    onSignedOut: notifyUnauthorized,
    window,
    document,
  });
  return { stream, live: new LiveStore(), notices: new NoticeStore() };
}

export function ServicesProvider({ services, children }: { services: Services; children: ReactNode }) {
  return <ServicesContext.Provider value={services}>{children}</ServicesContext.Provider>;
}

export function useServices(): Services {
  const services = useContext(ServicesContext);
  if (!services) throw new Error('ServicesProvider is missing');
  return services;
}

export function useConnectionState(): ConnectionState {
  const { stream } = useServices();
  return useSyncExternalStore(
    (listener) => stream.onStateChange(listener),
    () => stream.connectionState,
  );
}

/** The thread's in-flight turns from the live store, re-rendered at most once per frame. */
export function useLiveTurns(threadId: string): LiveTurn[] {
  const { live } = useServices();
  const version = useSyncExternalStore(live.subscribe, live.getVersion);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  return useMemo(() => live.forThread(threadId), [live, threadId, version]);
}

export function useNotices(): ThreadNotice[] {
  const { notices } = useServices();
  return useSyncExternalStore(notices.subscribe, notices.get);
}
