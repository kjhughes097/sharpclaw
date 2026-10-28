import { DropdownMenu } from 'radix-ui';
import { useEffect, useState, type ReactNode } from 'react';
import { Link, Outlet } from 'react-router';
import { usePendingApprovals, useSystem } from '../api/hooks';
import { budgetResetLocalTime } from '../api/errors';
import { useAuth } from '../app/auth';
import { useConnectionState } from '../app/services';
import { useTheme } from '../lib/theme';
import { formatAbsolute } from '../lib/time';
import { TabBar } from './TabBar';
import { Badge } from './ui';

const MENU_ITEM =
  'flex min-h-11 cursor-pointer items-center rounded px-3 text-sm outline-none data-[highlighted]:bg-surface-2';

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

/** `live`, `reconnecting…` or `offline`, from the stream manager. */
export function useConnectionLabel(): { label: string; tone: 'ok' | 'warn' | 'off' } {
  const state = useConnectionState();
  const online = useOnline();
  if (!online) return { label: 'offline', tone: 'off' };
  return state === 'open' ? { label: 'live', tone: 'ok' } : { label: 'reconnecting…', tone: 'warn' };
}

export const CONNECTION_DOT = { ok: 'bg-ok', warn: 'bg-warn', off: 'bg-muted' } as const;

export function ConnectionIndicator() {
  const { label, tone } = useConnectionLabel();
  return (
    <span className="inline-flex items-center gap-1.5" role="status" aria-live="polite">
      <span className={`h-2 w-2 rounded-full ${CONNECTION_DOT[tone]}`} aria-hidden="true" />
      {label}
    </span>
  );
}

function UserMenu() {
  const { state, logout } = useAuth();
  const system = useSystem();
  const { theme, toggle } = useTheme();
  const name = state.status === 'signed_in' ? (state.user?.name ?? 'admin key') : '';
  return (
    <DropdownMenu.Root modal={false}>
      <DropdownMenu.Trigger
        className="inline-flex min-h-11 items-center gap-1 rounded-md px-2 text-sm hover:bg-surface-2"
        aria-label={`Account menu (${name})`}
      >
        {name} <span aria-hidden="true">▾</span>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content
          align="end"
          className="z-30 min-w-60 rounded-md border border-border bg-surface p-1 shadow-lg"
        >
          <DropdownMenu.Item asChild className={MENU_ITEM}>
            <Link to="/account">Account</Link>
          </DropdownMenu.Item>
          <DropdownMenu.Item className={MENU_ITEM} onSelect={toggle}>
            {theme === 'dark' ? 'Light theme' : 'Dark theme'}
          </DropdownMenu.Item>
          {system.data && (
            <DropdownMenu.Label className="space-y-0.5 px-3 py-2 text-xs text-muted">
              <span className="block">
                SharpClaw {system.data.version} · {system.data.provider}
              </span>
              <span className="block">Server time {formatAbsolute(system.data.utcNow)}</span>
              <span className="block">Budgets reset {budgetResetLocalTime()} local</span>
            </DropdownMenu.Label>
          )}
          <DropdownMenu.Separator className="my-1 h-px bg-border" />
          <DropdownMenu.Item className={MENU_ITEM} onSelect={() => void logout()}>
            Sign out
          </DropdownMenu.Item>
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}

/**
 * The app frame (3.3.1 spec section 4): one header row (brand, connection state, version, the
 * fake-provider badge and the user menu), the tab bar, then the page. Also keeps the
 * `(N) SharpClaw` tab title.
 */
export function AppShell({ children }: { children?: ReactNode }) {
  const approvals = usePendingApprovals();
  const pending = approvals.data?.length ?? 0;
  const system = useSystem();

  useEffect(() => {
    document.title = pending > 0 ? `(${pending}) SharpClaw` : 'SharpClaw';
  }, [pending]);

  return (
    <div className="app-height flex flex-col">
      <a
        href="#main"
        className="sr-only focus:not-sr-only focus:absolute focus:top-2 focus:left-2 focus:z-50 focus:rounded focus:bg-surface focus:px-3 focus:py-2"
      >
        Skip to content
      </a>
      <header className="flex items-center gap-3 border-b border-border bg-surface px-3">
        <span className="font-semibold whitespace-nowrap text-accent">SharpClaw 3</span>
        <span className="flex min-w-0 flex-1 items-center gap-2 truncate text-sm text-muted">
          <ConnectionIndicator />
          {system.data && <span className="hidden sm:inline">· {system.data.version}</span>}
          {system.data?.provider === 'Fake' && <Badge tone="warn">Fake provider</Badge>}
        </span>
        <UserMenu />
      </header>
      <TabBar />
      <main id="main" tabIndex={-1} className="flex min-h-0 min-w-0 flex-1 flex-col outline-none">
        {children ?? <Outlet />}
      </main>
    </div>
  );
}
