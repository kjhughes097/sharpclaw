import { screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { api, call, hasCsrfToken } from '../src/api/client';
import { loginPath, safeNext } from '../src/app/auth';
import { apiError, server } from './msw';
import { renderApp } from './utils';

const signedOut = http.get('*/api/auth/session', () => HttpResponse.json({ authenticated: false }));

describe('safeNext', () => {
  it.each([
    ['/prompts/abc', '/prompts/abc'],
    ['/audit?tab=tools', '/audit?tab=tools'],
    ['//evil.example', '/overview'],
    ['//evil.example/chats', '/overview'],
    ['/\\evil.example', '/overview'],
    ['https://evil.example', '/overview'],
    ['javascript:alert(1)', '/overview'],
    ['JAVASCRIPT:alert(1)', '/overview'],
    ['chats', '/overview'],
    ['', '/overview'],
    [null, '/overview'],
    ['/login?next=/x', '/overview'],
    ['/\u0000x', '/overview'],
  ])('%s → %s', (input, expected) => {
    expect(safeNext(input)).toBe(expected);
  });

  it('builds login paths with an encoded next', () => {
    expect(loginPath('/prompts/abc')).toBe('/login?next=%2Fprompts%2Fabc');
    expect(loginPath('/')).toBe('/login');
  });
});

describe('auth gate', () => {
  it('sends a signed-out visitor to the login page with next', async () => {
    server.use(signedOut);
    renderApp('/agents/assistant');
    await waitFor(() =>
      expect(screen.getByTestId('location')).toHaveTextContent('/login?next=%2Fagents%2Fassistant'),
    );
    expect(screen.getByRole('heading', { name: 'Sign in to SharpClaw' })).toBeInTheDocument();
  });

  it('logs in, keeps the CSRF token in memory only and goes to next', async () => {
    server.use(
      signedOut,
      http.post('*/api/auth/login', async ({ request }) => {
        const body = (await request.json()) as { username: string; password: string };
        expect(body).toEqual({ username: 'kevin', password: 'correct horse battery staple' });
        return HttpResponse.json({
          user: { id: 'u-1', name: 'kevin' },
          csrfToken: 'csrf-xyz',
          expiresAt: '2026-11-01T00:00:00Z',
        });
      }),
    );
    const { user } = renderApp('/login?next=%2Faudit');
    await user.type(await screen.findByLabelText('Username'), 'kevin');
    await user.type(screen.getByLabelText('Password'), 'correct horse battery staple');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/audit'));
    expect(hasCsrfToken()).toBe(true);
    // Never in browser storage or the URL.
    expect(JSON.stringify({ ...localStorage })).not.toContain('csrf-xyz');
    expect(JSON.stringify({ ...sessionStorage })).not.toContain('csrf-xyz');
    expect(window.location.href).not.toContain('csrf-xyz');
  });

  it('ignores an unsafe next after login', async () => {
    server.use(
      signedOut,
      http.post('*/api/auth/login', () =>
        HttpResponse.json({
          user: { id: 'u-1', name: 'kevin' },
          csrfToken: 'c',
          expiresAt: '2026-11-01T00:00:00Z',
        }),
      ),
    );
    const { user } = renderApp('/login?next=%2F%2Fevil.example');
    await user.type(await screen.findByLabelText('Username'), 'kevin');
    await user.type(screen.getByLabelText('Password'), 'pw');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent(/^\/overview$/));
  });

  it('shows a generic error and the rate-limit wait', async () => {
    server.use(
      signedOut,
      http.post('*/api/auth/login', () => apiError(401, 'unauthorized', 'Invalid username or password.')),
    );
    const { user } = renderApp('/login');
    await user.type(await screen.findByLabelText('Username'), 'kevin');
    await user.type(screen.getByLabelText('Password'), 'nope');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Invalid username or password.');

    server.use(
      http.post('*/api/auth/login', () => apiError(429, 'rate_limited', 'x', { 'Retry-After': '600' })),
    );
    await user.type(screen.getByLabelText('Password'), 'nope');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Too many attempts. Try again in 10 minutes.');
  });

  it('a 401 from any call signs out once and returns to login', async () => {
    let sessionCalls = 0;
    server.use(
      http.get('*/api/auth/session', () => {
        sessionCalls++;
        return HttpResponse.json({
          authenticated: true,
          method: 'session',
          user: { id: 'u', name: 'kevin' },
          csrfToken: 'c',
        });
      }),
      http.get('*/api/threads', () => apiError(401, 'unauthorized')),
      http.get('*/api/approvals', () => apiError(401, 'unauthorized')),
    );
    const { services } = renderApp('/audit');
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/login?next=%2Faudit'));
    expect(services.stream.connectionState).not.toBe('open');
    expect(hasCsrfToken()).toBe(false);
    expect(sessionCalls).toBe(1);
  });

  it('a csrf_failed re-checks the session and retries once', async () => {
    let attempts = 0;
    const tokens: (string | null)[] = [];
    server.use(
      http.patch('*/api/threads/:id', ({ request }) => {
        attempts++;
        tokens.push(request.headers.get('X-CSRF-Token'));
        return attempts === 1
          ? apiError(403, 'csrf_failed')
          : HttpResponse.json({
              thread: { id: 't', agentId: 'a', title: 'x', createdAt: '', archived: false },
            });
      }),
      http.get('*/api/auth/session', () =>
        HttpResponse.json({ authenticated: true, method: 'session', csrfToken: 'fresh-token' }),
      ),
    );
    await call(() =>
      api.PATCH('/api/threads/{id}', { params: { path: { id: 't' } }, body: { title: 'x', archived: null } }),
    );
    expect(attempts).toBe(2);
    expect(tokens[1]).toBe('fresh-token');
  });
});
