import { screen, waitFor, within } from '@testing-library/react';
import { axe } from 'jest-axe';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { splitAgentTabs, tabLabel } from '../src/components/TabBar';
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

  it('is dark by default and switches to a remembered light theme from the user menu', async () => {
    const { user } = renderApp('/overview');
    expect(document.documentElement.dataset.theme).toBeUndefined();
    await user.click(await screen.findByRole('button', { name: 'Account menu (kevin)' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Light theme' }));
    expect(document.documentElement.dataset.theme).toBe('light');
    expect(localStorage.getItem('sharpclaw.theme')).toBe('light');
    await user.click(screen.getByRole('button', { name: 'Account menu (kevin)' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Dark theme' }));
    expect(document.documentElement.dataset.theme).toBeUndefined();
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
