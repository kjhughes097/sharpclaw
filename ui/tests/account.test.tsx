import { screen, waitFor } from '@testing-library/react';
import { axe } from 'jest-axe';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { apiError, server } from './msw';
import { renderApp } from './utils';

// jest-axe's matcher is registered in tests/setup.ts; its types only cover Jest's `expect`.

describe('account page', () => {
  it('shows the user, session expiry and system info', async () => {
    const { container } = renderApp('/account');
    expect(await screen.findByRole('heading', { level: 1, name: 'Account' })).toBeInTheDocument();
    expect(screen.getByText('kevin')).toBeInTheDocument();
    expect(screen.getByText('Session expires').nextElementSibling?.textContent).toMatch(/2026/);
    expect(await screen.findByText('abc1234')).toBeInTheDocument();
    expect(screen.getByText('Fake')).toBeInTheDocument();
    expect(screen.getByText('Server time').nextElementSibling?.textContent).toMatch(/2026/);
    expect(screen.getByText("You're connected over plain HTTP on your local network.")).toBeInTheDocument();
    expect(screen.getByLabelText('Current password')).toHaveAttribute('autocomplete', 'current-password');
    expect(screen.getByLabelText('New password')).toHaveAttribute('autocomplete', 'new-password');
    expect(screen.getByLabelText('Confirm new password')).toHaveAttribute('autocomplete', 'new-password');
    expect(await axe(container)).toHaveNoViolations();
  });

  it('validates the new password length and confirmation on the client', async () => {
    let posted = false;
    server.use(
      http.post('*/api/auth/password', () => {
        posted = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const { user } = renderApp('/account');
    await user.type(await screen.findByLabelText('Current password'), 'old password here');
    await user.type(screen.getByLabelText('New password'), 'short');
    await user.type(screen.getByLabelText('Confirm new password'), 'different');
    await user.click(screen.getByRole('button', { name: 'Change password' }));
    expect(screen.getByText('Use at least 12 characters.')).toBeInTheDocument();
    expect(screen.getByText('The passwords do not match.')).toBeInTheDocument();
    expect(screen.getByLabelText('New password')).toHaveAttribute('aria-invalid', 'true');
    expect(posted).toBe(false);
  });

  it('changes the password and shows a toast', async () => {
    let body: unknown;
    server.use(
      http.post('*/api/auth/password', async ({ request }) => {
        body = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const { user } = renderApp('/account');
    await user.type(await screen.findByLabelText('Current password'), 'old password here');
    await user.type(screen.getByLabelText('New password'), 'a much longer password');
    await user.type(screen.getByLabelText('Confirm new password'), 'a much longer password');
    await user.click(screen.getByRole('button', { name: 'Change password' }));
    expect(await screen.findByText('Password changed.')).toBeInTheDocument();
    expect(body).toEqual({ currentPassword: 'old password here', newPassword: 'a much longer password' });
    expect(screen.getByLabelText('New password')).toHaveValue('');
  });

  it('shows a wrong current password plainly and stays signed in', async () => {
    server.use(
      http.post('*/api/auth/password', () => apiError(401, 'unauthorized', 'Current password is incorrect.')),
    );
    const { user } = renderApp('/account');
    await user.type(await screen.findByLabelText('Current password'), 'wrong password!');
    await user.type(screen.getByLabelText('New password'), 'a much longer password');
    await user.type(screen.getByLabelText('Confirm new password'), 'a much longer password');
    await user.click(screen.getByRole('button', { name: 'Change password' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Current password is incorrect.');
    expect(screen.getByTestId('location')).toHaveTextContent('/account');
  });

  it('shows server policy errors', async () => {
    server.use(
      http.post('*/api/auth/password', () => apiError(400, 'validation_failed', 'Password is too common.')),
    );
    const { user } = renderApp('/account');
    await user.type(await screen.findByLabelText('Current password'), 'old password here');
    await user.type(screen.getByLabelText('New password'), 'password1234');
    await user.type(screen.getByLabelText('Confirm new password'), 'password1234');
    await user.click(screen.getByRole('button', { name: 'Change password' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Password is too common.');
  });

  it('signs out through the API and lands on the login page', async () => {
    let loggedOut = false;
    server.use(
      http.post('*/api/auth/logout', () => {
        loggedOut = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const { user } = renderApp('/account');
    await user.click(await screen.findByRole('button', { name: 'Sign out' }));
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent(/^\/login(\?|$)/));
    expect(loggedOut).toBe(true);
    expect(screen.getByRole('heading', { name: 'Sign in to SharpClaw' })).toBeInTheDocument();
  });
});
