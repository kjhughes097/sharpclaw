import { act, screen, waitFor, within } from '@testing-library/react';
import { axe } from 'jest-axe';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { setServerTime } from '../src/lib/time';
import { apiError, fixtures, server } from './msw';
import { renderApp, renderWithProviders } from './utils';
import { ApprovalCard } from '../src/features/approvals/ApprovalCard';

function decisions() {
  const posted: { id: string; decision: string }[] = [];
  server.use(
    http.post('*/api/approvals/:id', async ({ params, request }) => {
      const body = (await request.json()) as { decision: string };
      posted.push({ id: String(params.id), decision: body.decision });
      return HttpResponse.json({
        approval: { id: params.id, status: body.decision, decidedBy: 'user', expiresAt: '' },
      });
    }),
  );
  return posted;
}

describe('approvals', () => {
  it('approves and denies from the Approvals page', async () => {
    server.use(
      http.get('*/api/approvals', () =>
        HttpResponse.json({
          approvals: [
            fixtures.approval(),
            fixtures.approval({ id: 'approval-2', toolName: 'workspace_write' }),
          ],
        }),
      ),
    );
    const posted = decisions();
    const { user } = renderApp('/approvals');
    const first = await screen.findByRole('region', { name: 'Approval for workspace_delete' });
    await user.click(within(first).getByRole('button', { name: 'Approve' }));
    const second = screen.getByRole('region', { name: 'Approval for workspace_write' });
    await user.click(within(second).getByRole('button', { name: 'Deny' }));
    await waitFor(() =>
      expect(posted).toEqual([
        { id: 'approval-1', decision: 'approved' },
        { id: 'approval-2', decision: 'denied' },
      ]),
    );
    expect(within(first).getByRole('link', { name: 'Open thread' })).toHaveAttribute(
      'href',
      '/chats/thread-1',
    );
  });

  it('the highest risk needs a second confirmation', async () => {
    setServerTime('2026-10-25T12:00:00Z');
    const posted = decisions();
    const { user } = renderWithProviders(
      <ApprovalCard approval={{ ...fixtures.approval({ risk: 'critical' }) }} />,
    );
    await user.click(await screen.findByRole('button', { name: 'Approve' }));
    expect(screen.getByRole('alert')).toHaveTextContent('This is a critical-risk action. Approve it?');
    expect(posted).toEqual([]);
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('alert')).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Approve' }));
    await user.click(screen.getByRole('button', { name: 'Yes, approve' }));
    await waitFor(() => expect(posted).toEqual([{ id: 'approval-1', decision: 'approved' }]));
  });

  it('a 409 shows "Already decided."', async () => {
    setServerTime('2026-10-25T12:00:00Z');
    server.use(
      http.post('*/api/approvals/:id', () => apiError(409, 'conflict', 'Approval is already expired.')),
    );
    const { user } = renderWithProviders(<ApprovalCard approval={fixtures.approval()} />);
    await user.click(await screen.findByRole('button', { name: 'Deny' }));
    expect(await screen.findByText('Already decided.')).toBeInTheDocument();
  });

  it('counts down using the server clock and mutes once expired', async () => {
    // Browser clock is "now"; the server is 10 minutes ahead of the fixture's creation time.
    setServerTime('2026-10-25T12:03:00Z');
    renderWithProviders(<ApprovalCard approval={fixtures.approval({ expiresAt: '2026-10-25T12:04:00Z' })} />);
    expect(await screen.findByText(/expires in (1:00|0:59)/)).toBeInTheDocument();
    act(() => setServerTime('2026-10-25T12:05:00Z'));
    expect(
      await screen.findByText('Expired — the agent was told', {}, { timeout: 2000 }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approve' })).toBeNull();
  });

  it('shows decided approvals as decided', () => {
    renderWithProviders(<ApprovalCard approval={fixtures.approval({ status: 'denied' })} />);
    expect(screen.getByText('Denied')).toBeInTheDocument();
  });

  it('nav badge and tab title count pending approvals', async () => {
    server.use(
      http.get('*/api/approvals', () =>
        HttpResponse.json({ approvals: [fixtures.approval(), fixtures.approval({ id: 'approval-2' })] }),
      ),
    );
    renderApp('/agents');
    await waitFor(() => expect(document.title).toBe('(2) SharpClaw'));
    expect(screen.getAllByLabelText('2 pending').length).toBeGreaterThan(0);
  });

  it('refreshes when another tab decides (approval.decided event)', async () => {
    let pending = [fixtures.approval()];
    server.use(http.get('*/api/approvals', () => HttpResponse.json({ approvals: pending })));
    const { services } = renderApp('/approvals');
    await screen.findByRole('region', { name: 'Approval for workspace_delete' });
    pending = [];
    act(() =>
      services.source().emit(
        'approval.decided',
        {
          approvalId: 'approval-1',
          correlationId: 'm-1',
          status: 'approved',
          decidedBy: 'user',
          threadId: 'thread-1',
        },
        30,
      ),
    );
    expect(await screen.findByText('Nothing is waiting for you.')).toBeInTheDocument();
    await waitFor(() => expect(document.title).toBe('SharpClaw'));
  });

  it('shows the inline card in the thread at the in-flight turn', async () => {
    setServerTime('2026-10-25T12:00:00Z');
    server.use(
      http.get('*/api/threads/:id', () =>
        HttpResponse.json({
          thread: {
            id: 'thread-1',
            agentId: 'agent-1',
            title: 'T',
            createdAt: '',
            archived: false,
            context: null,
          },
          agent: fixtures.agent(),
          inFlight: { correlationId: 'u1', claimedAt: '2026-10-25T11:59:00Z' },
          queued: 0,
          pendingApprovals: [
            {
              id: 'approval-1',
              correlationId: 'u1',
              toolName: 'workspace_delete',
              risk: 'high',
              summary: 'Delete notes/a.md',
              reason: 'Deletes a file',
              createdAt: '2026-10-25T11:59:30Z',
              expiresAt: '2026-10-25T12:04:30Z',
            },
          ],
        }),
      ),
      http.get('*/api/threads/:id/messages', () =>
        HttpResponse.json({
          messages: [fixtures.message({ id: 'u1', seq: 1, status: 'claimed', content: 'clean up' })],
          hasMoreBefore: false,
        }),
      ),
    );
    renderApp('/chats/thread-1');
    const card = await screen.findByRole('region', { name: 'Approval for workspace_delete' });
    expect(within(card).getByText('Why: Deletes a file')).toBeInTheDocument();
    expect(within(card).getByText('risk: high')).toBeInTheDocument();
  });

  it('the Approvals page has an empty state and no accessibility violations', async () => {
    const { container } = renderApp('/approvals');
    expect(await screen.findByText('Nothing is waiting for you.')).toBeInTheDocument();
    expect(screen.getByText(/Approvals appear here when an agent needs your permission/)).toBeInTheDocument();
    expect(await axe(container)).toHaveNoViolations();
  });
});
