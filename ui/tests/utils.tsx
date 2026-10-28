import { render } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { AppProviders, AppRoutes, createQueryClient } from '../src/App';
import { LiveStore } from '../src/api/live';
import { EventStreamManager, type EventSourceLike } from '../src/api/stream';
import { NoticeStore, type Services } from '../src/app/services';

/** A controllable stand-in for the browser's EventSource. */
export class FakeEventSource implements EventSourceLike {
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSED = 2;

  readyState = FakeEventSource.CONNECTING;
  onopen: ((ev: Event) => unknown) | null = null;
  onerror: ((ev: Event) => unknown) | null = null;
  closed = false;
  private readonly listeners = new Map<string, ((ev: MessageEvent) => void)[]>();

  constructor(readonly url: string) {}

  addEventListener(type: string, listener: (ev: MessageEvent) => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]);
  }

  close() {
    this.closed = true;
    this.readyState = FakeEventSource.CLOSED;
  }

  /** The server accepted the connection. */
  open() {
    this.readyState = FakeEventSource.OPEN;
    this.onopen?.(new Event('open'));
  }

  /** Sends one SSE event; `id` makes it durable. */
  emit(type: string, data: unknown, id?: string | number) {
    const ev = new MessageEvent(type, {
      data: typeof data === 'string' ? data : JSON.stringify(data),
      lastEventId: id === undefined ? '' : String(id),
    });
    for (const listener of this.listeners.get(type) ?? []) listener(ev);
  }

  /** A transient drop: the browser keeps retrying by itself. */
  drop() {
    this.readyState = FakeEventSource.CONNECTING;
    this.onerror?.(new Event('error'));
  }

  /** A non-200 response: the browser gives up. */
  fail() {
    this.readyState = FakeEventSource.CLOSED;
    this.onerror?.(new Event('error'));
  }
}

export interface TestServices extends Services {
  sources: FakeEventSource[];
  /** The newest EventSource the manager opened. */
  source: () => FakeEventSource;
}

export function createTestServices(checkSession: () => Promise<boolean> = async () => true): TestServices {
  const sources: FakeEventSource[] = [];
  const stream = new EventStreamManager({
    createEventSource: (url) => {
      const source = new FakeEventSource(url);
      sources.push(source);
      return source;
    },
    checkSession,
    onSignedOut: () => {},
  });
  // Synchronous live updates keep tests deterministic.
  const live = new LiveStore((callback) => callback());
  return {
    stream,
    live,
    notices: new NoticeStore(),
    sources,
    source: () => {
      const last = sources[sources.length - 1];
      if (!last) throw new Error('No EventSource opened yet');
      return last;
    },
  };
}

/** Shows the current location so tests can assert redirects. */
function LocationProbe() {
  const location = useLocation();
  return <output data-testid="location">{location.pathname + location.search}</output>;
}

/** Renders the whole app at `route` with fake services and a fresh query cache. */
export function renderApp(route = '/chats', options: { services?: TestServices } = {}) {
  const services = options.services ?? createTestServices();
  const queryClient = createQueryClient();
  queryClient.setDefaultOptions({ queries: { ...queryClient.getDefaultOptions().queries, retry: false } });
  const user = userEvent.setup();
  const utils = render(
    <MemoryRouter initialEntries={[route]}>
      <AppProviders queryClient={queryClient} services={services}>
        <AppRoutes />
        <LocationProbe />
      </AppProviders>
    </MemoryRouter>,
  );
  return { ...utils, user, services, queryClient };
}

/** Renders a component inside the providers (signed in), without the app routes. */
export function renderWithProviders(ui: ReactNode, options: { route?: string; path?: string; services?: TestServices } = {}) {
  const services = options.services ?? createTestServices();
  const queryClient = createQueryClient();
  queryClient.setDefaultOptions({ queries: { ...queryClient.getDefaultOptions().queries, retry: false } });
  const user = userEvent.setup();
  const utils = render(
    <MemoryRouter initialEntries={[options.route ?? '/']}>
      <AppProviders queryClient={queryClient} services={services}>
        <Routes>
          <Route path={options.path ?? '*'} element={ui} />
        </Routes>
        <LocationProbe />
      </AppProviders>
    </MemoryRouter>,
  );
  return { ...utils, user, services, queryClient };
}
