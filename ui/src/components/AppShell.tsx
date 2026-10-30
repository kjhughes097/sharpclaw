import { useEffect, type ReactNode } from 'react';
import { Outlet } from 'react-router';
import { usePendingApprovals, useSystem } from '../api/hooks';
import { useTheme } from '../lib/theme';
import { LcarsShell } from './LcarsShell';
import { ConnectionIndicator, UserMenu } from './ShellParts';
import { TabBar } from './TabBar';
import { Badge } from './ui';

/**
 * The app frame (3.3.1 spec section 4): one header row (brand, connection state, version, the
 * fake-provider badge and the user menu), the tab bar, then the page. Also keeps the
 * `(N) SharpClaw` tab title.
 */
export function AppShell({ children }: { children?: ReactNode }) {
  const approvals = usePendingApprovals();
  const pending = approvals.data?.length ?? 0;
  const system = useSystem();

  const { theme } = useTheme();

  useEffect(() => {
    document.title = pending > 0 ? `(${pending}) SharpClaw` : 'SharpClaw';
  }, [pending]);

  // LCARS is a different frame (sidebar, elbows and bars), not just different colours.
  if (theme === 'lcars') return <LcarsShell>{children}</LcarsShell>;

  return (
    // relative + overflow-hidden: the shell is exactly the viewport and never scrolls itself; only
    // the panes inside it do.
    <div className="app-height relative flex flex-col overflow-hidden">
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
