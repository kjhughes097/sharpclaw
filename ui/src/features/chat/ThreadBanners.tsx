import { budgetResetLocalTime } from '../../api/errors';
import type { ThreadDetailResponse } from '../../api/types';
import { useServerNow } from '../../lib/time';

/** An agent with queued work that hasn't polled for this long is shown as away. */
const AWAY_MS = 3 * 60_000;

/** Status banners above the messages: agent disabled, budget used up, agent away. */
export function ThreadBanners({ agent, queued }: { agent: ThreadDetailResponse['agent']; queued: number }) {
  const now = useServerNow();
  const lastSeen = agent.lastSeenAt ? Date.parse(agent.lastSeenAt) : null;
  const away = queued > 0 && (lastSeen === null || now - lastSeen > AWAY_MS);
  const budgetUsed = agent.dailyBudgetUsd > 0 ? agent.remainingUsd <= 0 : agent.dailyBudgetUsd === 0;
  return (
    <div className="space-y-1 px-3 pt-2 empty:hidden">
      {!agent.enabled && (
        <p role="status" className="rounded-md bg-warn-soft p-2 text-sm">
          This agent is disabled. Messages will queue until it is enabled.
        </p>
      )}
      {budgetUsed && (
        <p role="status" className="rounded-md bg-warn-soft p-2 text-sm">
          Daily budget used up. Resets at {budgetResetLocalTime()}.
        </p>
      )}
      {away && (
        <p role="status" className="rounded-md bg-warn-soft p-2 text-sm">
          The agent hasn&apos;t connected recently.
        </p>
      )}
    </div>
  );
}
