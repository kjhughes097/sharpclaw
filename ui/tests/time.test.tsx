import { act, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { LastSeen } from '../src/features/agents/AgentParts';
import { ApprovalCard } from '../src/features/approvals/ApprovalCard';
import { setServerTime } from '../src/lib/time';
import { fixtures } from './msw';
import { renderWithProviders } from './utils';

describe('server clock', () => {
  it('re-renders last-seen state when the server time arrives after the first render', () => {
    // Seen one minute before the server's "now", but days before the browser's own clock.
    const serverNow = '2020-01-01T12:00:00Z';
    render(<LastSeen iso="2020-01-01T11:59:00Z" />);
    expect(screen.getByText(/Not seen recently/)).toBeInTheDocument();

    act(() => setServerTime(serverNow));

    expect(screen.queryByText(/Not seen recently/)).not.toBeInTheDocument();
    expect(screen.getByText(/min/)).toBeInTheDocument();
  });

  it('an approval card rendered before the server time arrives switches from expired to pending', () => {
    // The fixture expires at 12:04 on the server's clock, long before the browser's own "now".
    renderWithProviders(<ApprovalCard approval={fixtures.approval()} />);
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();

    act(() => setServerTime('2026-10-25T12:00:00Z'));

    expect(screen.getByRole('button', { name: 'Approve' })).toBeInTheDocument();
    expect(screen.getByText(/expires in 4:00/)).toBeInTheDocument();
  });
});
