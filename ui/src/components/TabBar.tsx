import { DropdownMenu } from 'radix-ui';
import { useEffect, useRef } from 'react';
import { Link, NavLink, useLocation } from 'react-router';
import { useAgents, usePendingApprovals } from '../api/hooks';
import type { AgentDto } from '../api/types';

/** Enabled agents shown inline as tabs; the rest (and every disabled agent) go into "More". */
export const MAX_INLINE_AGENT_TABS = 6;

const TAB =
  'inline-flex min-h-11 shrink-0 items-center gap-1.5 border-b-2 px-3 text-sm whitespace-nowrap outline-none focus-visible:outline-2';
const ACTIVE = 'border-accent text-accent';
const IDLE = 'border-transparent text-fg hover:text-accent';

const MENU_ITEM =
  'flex min-h-11 cursor-pointer items-center gap-2 rounded px-3 text-sm outline-none data-[highlighted]:bg-surface-2';

/** `ade` → `Ade`: tab labels are the agent name, capitalised. */
export function tabLabel(name: string): string {
  return name.charAt(0).toUpperCase() + name.slice(1);
}

/** Splits agents into inline tabs and the "More" menu (3.3.1 spec section 4.2). */
export function splitAgentTabs(agents: readonly AgentDto[]): { inline: AgentDto[]; more: AgentDto[] } {
  const byName = [...agents].sort((a, b) => a.name.localeCompare(b.name));
  const enabled = byName.filter((a) => a.enabled);
  const inline = enabled.slice(0, MAX_INLINE_AGENT_TABS);
  const more = [...enabled.slice(MAX_INLINE_AGENT_TABS), ...byName.filter((a) => !a.enabled)];
  return { inline, more };
}

function Tab({ to, children, end = false }: { to: string; children: React.ReactNode; end?: boolean }) {
  return (
    <NavLink to={to} end={end} className={({ isActive }) => `${TAB} ${isActive ? ACTIVE : IDLE}`}>
      {children}
    </NavLink>
  );
}

function agentPath(name: string) {
  return `/agents/${encodeURIComponent(name)}`;
}

function MoreMenu({ agents, activeName }: { agents: AgentDto[]; activeName: string | null }) {
  const active = agents.find((a) => a.name === activeName);
  return (
    <DropdownMenu.Root modal={false}>
      <DropdownMenu.Trigger
        className={`${TAB} ${active ? ACTIVE : IDLE}`}
        aria-current={active ? 'page' : undefined}
      >
        {active ? `More: ${active.name}` : 'More'} <span aria-hidden="true">▾</span>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content
          align="start"
          className="z-30 min-w-48 rounded-md border border-border bg-surface p-1 shadow-lg"
        >
          {agents.map((agent) => (
            <DropdownMenu.Item key={agent.id} asChild className={MENU_ITEM}>
              <Link
                to={agentPath(agent.name)}
                title={agent.enabled ? undefined : 'disabled'}
                className={agent.enabled ? '' : 'text-muted'}
              >
                {tabLabel(agent.name)}
                {!agent.enabled && <span className="text-xs">(disabled)</span>}
              </Link>
            </DropdownMenu.Item>
          ))}
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}

/**
 * The tab bar: Overview, Prompts, the agent tabs and "More", then Approvals (with the pending
 * count), Audit and Agents. Every tab is a link, so every view is a URL. On phones it is one
 * horizontally scrolling strip with the active tab scrolled into view.
 */
export function TabBar() {
  const agents = useAgents();
  const approvals = usePendingApprovals();
  const pending = approvals.data?.length ?? 0;
  const location = useLocation();
  const { inline, more } = splitAgentTabs(agents.data ?? []);
  const match = /^\/agents\/([^/]+)/.exec(location.pathname);
  const activeAgent = match?.[1] ? decodeURIComponent(match[1]) : null;
  const navRef = useRef<HTMLElement>(null);

  useEffect(() => {
    // scrollIntoView is missing in jsdom; it only matters on narrow screens anyway.
    navRef.current
      ?.querySelector('[aria-current="page"]')
      ?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
  }, [location.pathname]);

  return (
    <nav
      ref={navRef}
      aria-label="Main"
      className="flex items-stretch overflow-x-auto border-b border-border bg-surface px-2"
    >
      <Tab to="/overview">Overview</Tab>
      <Tab to="/prompts">Prompts</Tab>
      {inline.map((agent) => (
        <Tab key={agent.id} to={agentPath(agent.name)}>
          {tabLabel(agent.name)}
        </Tab>
      ))}
      {more.length > 0 && <MoreMenu agents={more} activeName={activeAgent} />}
      <span className="min-w-4 flex-1" aria-hidden="true" />
      <Tab to="/approvals">
        Approvals
        {pending > 0 && (
          <span
            className="rounded-full bg-danger px-1.5 text-xs text-surface"
            aria-label={`${pending} pending`}
          >
            {pending}
          </span>
        )}
      </Tab>
      <Tab to="/audit">Audit</Tab>
      <Tab to="/agents" end>
        Agents
      </Tab>
    </nav>
  );
}
