import { Dialog as RadixDialog } from 'radix-ui';
import { useEffect, useState, type ReactNode } from 'react';
import { NavLink, Outlet, useLocation } from 'react-router';
import { usePendingApprovals, useSystem } from '../api/hooks';
import { useConnectionState } from '../app/services';
import { Badge } from './ui';

const NAV = [
  { to: '/chats', label: 'Chats' },
  { to: '/approvals', label: 'Approvals' },
  { to: '/agents', label: 'Agents' },
  { to: '/audit', label: 'Audit' },
  { to: '/account', label: 'Account' },
] as const;

function useOnline() {
  const [online, setOnline] = useState(typeof navigator === 'undefined' ? true : navigator.onLine);
  useEffect(() => {
    const on = () => setOnline(true);
    const off = () => setOnline(false);
    window.addEventListener('online', on);
    window.addEventListener('offline', off);
    return () => {
      window.removeEventListener('online', on);
      window.removeEventListener('offline', off);
    };
  }, []);
  return online;
}

/** `Live`, `Reconnecting…` or `Offline`, from the stream manager (3.3 spec section 9.1). */
export function ConnectionIndicator() {
  const state = useConnectionState();
  const online = useOnline();
  const label = !online ? 'Offline' : state === 'open' ? 'Live' : 'Reconnecting…';
  const dot = !online ? 'bg-danger' : state === 'open' ? 'bg-ok' : 'bg-warn';
  return (
    <span className="inline-flex items-center gap-1.5 text-xs text-muted" role="status" aria-live="polite">
      <span className={`h-2 w-2 rounded-full ${dot}`} aria-hidden="true" />
      {label}
    </span>
  );
}

function NavItems({ pending, onNavigate }: { pending: number; onNavigate?: () => void }) {
  return (
    <ul className="space-y-1">
      {NAV.map((item) => (
        <li key={item.to}>
          <NavLink
            to={item.to}
            onClick={onNavigate}
            className={({ isActive }) =>
              `flex min-h-11 items-center justify-between rounded-md px-3 text-sm font-medium ${
                isActive ? 'bg-accent-soft text-fg' : 'text-fg hover:bg-surface-2'
              }`
            }
          >
            {item.label}
            {item.to === '/approvals' && pending > 0 && (
              <span className="rounded-full bg-danger px-2 text-xs text-surface" aria-label={`${pending} pending`}>
                {pending}
              </span>
            )}
          </NavLink>
        </li>
      ))}
    </ul>
  );
}

function FakeBadge() {
  const system = useSystem();
  if (system.data?.provider !== 'Fake') return null;
  return <Badge tone="warn">Fake provider</Badge>;
}

function Footer() {
  const system = useSystem();
  if (!system.data) return null;
  return (
    <p className="text-xs text-muted">
      SharpClaw {system.data.version} · {system.data.provider}
    </p>
  );
}

/**
 * The app frame: a sidebar on desktop, a top bar with a drawer on mobile, a skip link, the
 * connection indicator, the pending-approvals badge and the `(N) SharpClaw` tab title.
 */
export function AppShell({ children }: { children?: ReactNode }) {
  const [drawerOpen, setDrawerOpen] = useState(false);
  const approvals = usePendingApprovals();
  const pending = approvals.data?.length ?? 0;
  const location = useLocation();

  useEffect(() => {
    document.title = pending > 0 ? `(${pending}) SharpClaw` : 'SharpClaw';
  }, [pending]);

  useEffect(() => setDrawerOpen(false), [location.pathname]);

  return (
    <div className="app-height flex flex-col md:flex-row">
      <a
        href="#main"
        className="sr-only focus:not-sr-only focus:absolute focus:top-2 focus:left-2 focus:z-50 focus:rounded focus:bg-surface focus:px-3 focus:py-2"
      >
        Skip to content
      </a>

      {/* Desktop sidebar */}
      <aside className="hidden w-56 shrink-0 flex-col justify-between border-r border-border bg-surface p-3 md:flex">
        <div>
          <div className="mb-4 flex items-center justify-between px-2">
            <span className="text-lg font-semibold">SharpClaw</span>
          </div>
          <nav aria-label="Main">
            <NavItems pending={pending} />
          </nav>
        </div>
        <div className="space-y-2 px-2">
          <ConnectionIndicator />
          <FakeBadge />
          <Footer />
        </div>
      </aside>

      {/* Mobile top bar */}
      <header className="flex items-center justify-between gap-2 border-b border-border bg-surface px-3 py-1 md:hidden">
        <RadixDialog.Root open={drawerOpen} onOpenChange={setDrawerOpen}>
          <RadixDialog.Trigger
            className="inline-flex min-h-11 min-w-11 items-center justify-center rounded-md hover:bg-surface-2"
            aria-label={pending > 0 ? `Open navigation (${pending} approvals pending)` : 'Open navigation'}
          >
            <span aria-hidden="true">☰</span>
            {pending > 0 && <span className="ml-1 rounded-full bg-danger px-1.5 text-xs text-surface">{pending}</span>}
          </RadixDialog.Trigger>
          <RadixDialog.Portal>
            <RadixDialog.Overlay className="fixed inset-0 z-40 bg-black/50" />
            <RadixDialog.Content className="fixed inset-y-0 left-0 z-50 flex w-72 flex-col justify-between bg-surface p-3 shadow-xl">
              <div>
                <RadixDialog.Title className="mb-4 px-2 text-lg font-semibold">SharpClaw</RadixDialog.Title>
                <RadixDialog.Description className="sr-only">Navigation</RadixDialog.Description>
                <nav aria-label="Main">
                  <NavItems pending={pending} onNavigate={() => setDrawerOpen(false)} />
                </nav>
              </div>
              <div className="px-2">
                <Footer />
              </div>
            </RadixDialog.Content>
          </RadixDialog.Portal>
        </RadixDialog.Root>
        <span className="font-semibold">SharpClaw</span>
        <div className="flex items-center gap-2">
          <FakeBadge />
          <ConnectionIndicator />
        </div>
      </header>

      <main id="main" tabIndex={-1} className="flex min-h-0 min-w-0 flex-1 flex-col outline-none">
        {children ?? <Outlet />}
      </main>
    </div>
  );
}
