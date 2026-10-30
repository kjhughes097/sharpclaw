import { screen, waitFor, within } from '@testing-library/react';
import { axe } from 'jest-axe';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { splitAgentTabs, tabLabel } from '../src/components/TabBar';
import { getTheme } from '../src/lib/prefs';
import { chooseTheme, initTheme } from '../src/lib/theme';
import { fixtures, server } from './msw';
import { renderApp } from './utils';

const agents = (names: string[], disabled: string[] = []) =>
  [...names, ...disabled].map((name, i) =>
    fixtures.agent({ id: `a-${i}`, name, enabled: !disabled.includes(name) }),
  );

function tabNames() {
  return within(screen.getByRole('navigation', { name: 'Main' }))
    .getAllByRole('link')
    .map((l) => l.textContent);
}

describe('tab bar', () => {
  it('orders Overview, Prompts, agents (capitalised, alphabetical), then Approvals, Audit and Agents', async () => {
    server.use(http.get('*/api/agents', () => HttpResponse.json({ agents: agents(['fin', 'ade', 'cody']) })));
    renderApp('/overview');
    await screen.findByRole('link', { name: 'Ade' });
    expect(tabNames()).toEqual(['Overview', 'Prompts', 'Ade', 'Cody', 'Fin', 'Approvals', 'Audit', 'Agents']);
    expect(screen.getByRole('link', { name: 'Overview' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('link', { name: 'Cody' })).toHaveAttribute('href', '/agents/cody');
  });

  it('puts agents beyond six, and disabled agents, in More', () => {
    const list = agents(['a1', 'a2', 'a3', 'a4', 'a5', 'a6', 'a7'], ['zed']);
    const { inline, more } = splitAgentTabs(list);
    expect(inline.map((a) => a.name)).toEqual(['a1', 'a2', 'a3', 'a4', 'a5', 'a6']);
    expect(more.map((a) => a.name)).toEqual(['a7', 'zed']);
    expect(tabLabel('ade')).toBe('Ade');
  });

  it('shows "More: name" when the open agent tab lives in More, and greys disabled agents', async () => {
    server.use(
      http.get('*/api/agents', () => HttpResponse.json({ agents: agents(['ade'], ['homer']) })),
      http.get('*/api/agents/:name', () =>
        HttpResponse.json(fixtures.agent({ name: 'homer', enabled: false })),
      ),
    );
    const { user } = renderApp('/agents/homer');
    const more = await screen.findByRole('button', { name: /More: homer/ });
    expect(more).toHaveAttribute('aria-current', 'page');
    await user.click(more);
    const item = await screen.findByRole('menuitem', { name: /Homer/ });
    expect(item).toHaveTextContent('(disabled)');
    expect(item).toHaveAttribute('href', '/agents/homer');
  });

  it('counts pending approvals on the Approvals tab and in the title', async () => {
    server.use(
      http.get('*/api/approvals', () =>
        HttpResponse.json({ approvals: [fixtures.approval(), fixtures.approval({ id: 'approval-2' })] }),
      ),
    );
    renderApp('/overview');
    expect(await screen.findByLabelText('2 pending')).toBeInTheDocument();
    await waitFor(() => expect(document.title).toBe('(2) SharpClaw'));
  });
});

describe('routes', () => {
  it('shows not found inside the shell for a malformed agent name', async () => {
    server.use(
      http.get('*/api/agents/:name', () =>
        HttpResponse.json({ error: { code: 'not_found', message: 'x' } }, { status: 404 }),
      ),
    );
    renderApp('/agents/100%');
    expect(await screen.findByText(/Not found\. It may have been removed\./)).toBeInTheDocument();
    expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument();
  });

  it.each([
    ['/', '/overview'],
    ['/chats', '/prompts'],
    ['/chats/thread-1?panel=trace', '/prompts/thread-1?panel=trace'],
  ])('%s → %s', async (from, to) => {
    renderApp(from);
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent(to));
  });
});

describe('header', () => {
  it('shows the brand, connection state, version and the fake-provider badge', async () => {
    const { container } = renderApp('/overview');
    expect(await screen.findByText('SharpClaw 3')).toBeInTheDocument();
    expect(await screen.findByText('Fake provider')).toBeInTheDocument();
    expect(screen.getByText('· abc1234')).toBeInTheDocument();
    // The fake EventSource hasn't opened, so the stream is still connecting.
    expect(screen.getByText('reconnecting…')).toBeInTheDocument();
    expect(await axe(container)).toHaveNoViolations();
  });

  it('is the default theme unless another is picked, and remembers light and LCARS', async () => {
    const { user } = renderApp('/overview');
    expect(document.documentElement.dataset.theme).toBeUndefined();
    // LCARS swaps the whole shell, so the menu button is looked up afresh each time.
    const openMenu = async () =>
      user.click(await screen.findByRole('button', { name: 'Account menu (kevin)' }));
    await openMenu();
    expect(await screen.findByRole('menuitemradio', { name: /Default/ })).toBeChecked();

    await user.click(screen.getByRole('menuitemradio', { name: /Light/ }));
    expect(document.documentElement.dataset.theme).toBe('light');
    expect(localStorage.getItem('sharpclaw.theme')).toBe('light');

    await openMenu();
    await user.click(await screen.findByRole('menuitemradio', { name: /LCARS/ }));
    expect(document.documentElement.dataset.theme).toBe('lcars');
    expect(localStorage.getItem('sharpclaw.theme')).toBe('lcars');
    expect(await screen.findByText('SharpClaw / Overview')).toBeInTheDocument();

    await openMenu();
    expect(await screen.findByRole('menuitemradio', { name: /LCARS/ })).toBeChecked();
    await user.click(screen.getByRole('menuitemradio', { name: /Default/ }));
    expect(document.documentElement.dataset.theme).toBeUndefined();
    expect(localStorage.getItem('sharpclaw.theme')).toBe('dark');
    expect(await screen.findByText('SharpClaw 3')).toBeInTheDocument();
    localStorage.removeItem('sharpclaw.theme');
  });

  it('renders the LCARS frame with the same landmarks and controls, accessibly', async () => {
    localStorage.setItem('sharpclaw.theme', 'lcars');
    initTheme();
    try {
      const { container, user } = renderApp('/agents/ade');
      // The banner names the current section; the tabs, main landmark and menu are all there.
      expect(await screen.findByText('SharpClaw / Ade')).toBeInTheDocument();
      expect(screen.queryByText('SharpClaw 3')).not.toBeInTheDocument();
      expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument();
      expect(screen.getByRole('main')).toHaveAttribute('id', 'main');
      expect(await screen.findByText('Fake provider')).toBeInTheDocument();
      expect(await axe(container)).toHaveNoViolations();
      await user.click(screen.getByRole('button', { name: 'Account menu (kevin)' }));
      expect(await screen.findByRole('menuitemradio', { name: /LCARS/ })).toBeChecked();
    } finally {
      chooseTheme('dark');
      localStorage.removeItem('sharpclaw.theme');
    }
  });

  it('falls back to the default theme for an unknown stored value', () => {
    localStorage.setItem('sharpclaw.theme', 'neon');
    expect(getTheme()).toBe('dark');
    localStorage.removeItem('sharpclaw.theme');
  });

  it('has Account, system info and Sign out in the user menu', async () => {
    let loggedOut = false;
    server.use(
      http.post('*/api/auth/logout', () => {
        loggedOut = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const { user } = renderApp('/overview');
    await user.click(await screen.findByRole('button', { name: 'Account menu (kevin)' }));
    expect(await screen.findByRole('menuitem', { name: 'Account' })).toHaveAttribute('href', '/account');
    expect(screen.getByText('SharpClaw abc1234 · Fake')).toBeInTheDocument();
    await user.click(screen.getByRole('menuitem', { name: 'Sign out' }));
    await waitFor(() => expect(loggedOut).toBe(true));
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/login'));
  });
});
