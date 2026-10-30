import { DropdownMenu } from 'radix-ui';
import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import { useSystem } from '../api/hooks';
import { budgetResetLocalTime } from '../api/errors';
import { useAuth } from '../app/auth';
import { useConnectionState } from '../app/services';
import { THEMES, type Theme } from '../lib/prefs';
import { THEME_LABELS, useTheme } from '../lib/theme';
import { formatAbsolute } from '../lib/time';

/* Header pieces shared by the default shell and the LCARS shell. */

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

/** The account menu: Account, the theme picker, system info and Sign out. Shared by both shells. */
export function UserMenu() {
  const { state, logout } = useAuth();
  const system = useSystem();
  const { theme, choose } = useTheme();
  const name = state.status === 'signed_in' ? (state.user?.name ?? 'admin key') : '';
  return (
    <DropdownMenu.Root modal={false}>
      <DropdownMenu.Trigger
        className="inline-flex min-h-11 items-center gap-1 rounded-md px-2 text-sm hover:bg-surface-2 lcars:items-end lcars:rounded-full lcars:bg-lcars-blue lcars:px-4 lcars:pb-1 lcars:text-base lcars:text-accent-fg lcars:uppercase"
        aria-label={`Account menu (${name})`}
      >
        {name} <span aria-hidden="true">▾</span>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content
          align="end"
          className="z-30 min-w-60 rounded-md border border-border bg-surface p-1 shadow-lg lcars:rounded-l-2xl lcars:rounded-r-none lcars:border-0 lcars:border-l-8 lcars:border-lcars-violet lcars:bg-bg"
        >
          <DropdownMenu.Item asChild className={MENU_ITEM}>
            <Link to="/account">Account</Link>
          </DropdownMenu.Item>
          <DropdownMenu.Separator className="my-1 h-px bg-border" />
          <DropdownMenu.Label id="theme-label" className="px-3 pt-1 text-xs text-muted">
            Theme
          </DropdownMenu.Label>
          <DropdownMenu.RadioGroup
            aria-labelledby="theme-label"
            value={theme}
            onValueChange={(value) => choose(value as Theme)}
          >
            {THEMES.map((t) => (
              <DropdownMenu.RadioItem key={t} value={t} className={`${MENU_ITEM} gap-2`}>
                <span className="w-4" aria-hidden="true">
                  <DropdownMenu.ItemIndicator>✓</DropdownMenu.ItemIndicator>
                </span>
                {THEME_LABELS[t]}
              </DropdownMenu.RadioItem>
            ))}
          </DropdownMenu.RadioGroup>
          <DropdownMenu.Separator className="my-1 h-px bg-border" />
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
