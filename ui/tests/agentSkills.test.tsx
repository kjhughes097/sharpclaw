import { screen, within } from '@testing-library/react';
import { axe } from 'jest-axe';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { AgentDto } from '../src/api/types';
import { fixtures, server } from './msw';
import { renderApp } from './utils';

/** The agent page's Skills section (skills spec section 10). */
function serveAgent(over: Partial<AgentDto>) {
  server.use(http.get('*/api/agents/:name', () => HttpResponse.json(fixtures.agent(over))));
}

async function skillsRegion() {
  return screen.findByRole('region', { name: 'Skills' });
}

describe('agent skills', () => {
  it('lists each reported skill with its description and when it was reported', async () => {
    serveAgent({
      skillsReportedAt: '2026-10-25T11:55:00Z',
      skills: [
        { name: 'daily-notes', description: 'Keep a dated daily notes file.' },
        { name: 'research-brief', description: 'Write a short, sourced brief.' },
      ],
    });
    const { container } = renderApp('/agents/assistant');

    const region = await skillsRegion();
    const items = within(region).getAllByRole('listitem');
    expect(items).toHaveLength(2);
    expect(items[0]).toHaveTextContent('daily-notes');
    expect(items[0]).toHaveTextContent('Keep a dated daily notes file.');
    expect(items[1]).toHaveTextContent('research-brief');
    expect(within(region).getByText(/^Reported/)).toBeInTheDocument();
    expect(region.querySelector('time')).toHaveAttribute('dateTime', '2026-10-25T11:55:00Z');
    expect(await axe(container)).toHaveNoViolations();
  });

  it('says no skills were reported when the agent never reported', async () => {
    serveAgent({ skillsReportedAt: null, skills: [] });
    renderApp('/agents/assistant');

    const region = await skillsRegion();
    expect(within(region).getByText(/No skills reported/)).toBeInTheDocument();
    expect(within(region).queryByText(/^Reported/)).not.toBeInTheDocument();
    expect(within(region).queryByRole('list')).not.toBeInTheDocument();
  });

  it('says no skills were loaded when the report was empty', async () => {
    serveAgent({ skillsReportedAt: '2026-10-25T11:00:00Z', skills: [] });
    renderApp('/agents/assistant');

    const region = await skillsRegion();
    expect(within(region).getByText('No skills loaded.')).toBeInTheDocument();
    expect(within(region).queryByText(/No skills reported/)).not.toBeInTheDocument();
  });

  it('renders descriptions as plain text, never Markdown or HTML', async () => {
    const description = '**bold** <img src=x onerror=alert(1)> [link](http://evil)';
    serveAgent({ skillsReportedAt: '2026-10-25T11:00:00Z', skills: [{ name: 'tricky', description }] });
    renderApp('/agents/assistant');

    const region = await skillsRegion();
    expect(within(region).getByText(description)).toBeInTheDocument();
    expect(region.querySelector('img, strong, a')).toBeNull();
  });
});
