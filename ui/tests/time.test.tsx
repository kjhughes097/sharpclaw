import { act, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { LastSeen } from '../src/features/agents/AgentParts';
import { setServerTime } from '../src/lib/time';

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
});
