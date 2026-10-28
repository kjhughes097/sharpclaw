import { useEffect, useRef, useState } from 'react';
import { failReasonMessage } from '../../api/errors';
import type { LiveTurn } from '../../api/live';
import type { TraceToolCallDto } from '../../api/types';
import { Badge, Button, RelativeTime } from '../../components/ui';
import { AssistantText, Markdown, PlainText } from '../../lib/markdown';
import { serverNow } from '../../lib/time';
import { ApprovalCard, type ApprovalView } from '../approvals/ApprovalCard';
import { ToolActivity } from './ToolActivity';
import type { Turn } from './turns';

/** Streaming markdown is re-rendered at most ~10 times a second; the final text renders at once. */
export function useThrottledText(text: string, intervalMs = 100): string {
  const [shown, setShown] = useState(text);
  const last = useRef(0);
  useEffect(() => {
    const now = Date.now();
    const wait = intervalMs - (now - last.current);
    if (wait <= 0) {
      last.current = now;
      setShown(text);
      return;
    }
    const timer = setTimeout(() => {
      last.current = Date.now();
      setShown(text);
    }, wait);
    return () => clearTimeout(timer);
  }, [text, intervalMs]);
  return shown;
}

function Elapsed({ since }: { since: string }) {
  const [now, setNow] = useState(() => serverNow());
  useEffect(() => {
    const timer = setInterval(() => setNow(serverNow()), 1000);
    return () => clearInterval(timer);
  }, []);
  const seconds = Math.max(0, Math.floor((now - Date.parse(since)) / 1000));
  return <span>{seconds} s</span>;
}

function StreamingBubble({ text, since }: { text: string; since: string }) {
  const shown = useThrottledText(text);
  if (!text)
    return (
      <p className="text-sm text-muted">
        Thinking… <Elapsed since={since} />
      </p>
    );
  return (
    <div className="caret">
      <Markdown text={shown} />
    </div>
  );
}

export function TurnView({
  turn,
  live,
  approvals,
  trace,
  expandTools,
  claimedAt,
  highlighted,
  onResend,
}: {
  turn: Turn;
  live: LiveTurn | undefined;
  approvals: ApprovalView[];
  trace: Map<string, TraceToolCallDto> | undefined;
  expandTools: boolean;
  claimedAt: string | null;
  highlighted: boolean;
  onResend: (content: string) => void;
}) {
  const user = turn.user;
  const status = user?.status;
  const inFlight = status === 'claimed' || (!!live && !turn.reply);

  return (
    <li className="space-y-2" id={user ? `msg-${user.id}` : undefined} data-turn={turn.correlationId}>
      {user && (
        <div className="flex flex-col items-end">
          <div
            className={`max-w-[85%] rounded-2xl rounded-br-sm bg-accent-soft px-4 py-2 ${highlighted ? 'ring-2 ring-focus' : ''}`}
          >
            <span className="sr-only">You said: </span>
            <PlainText text={user.content} />
          </div>
          <div className="mt-1 flex items-center gap-2 text-xs text-muted">
            {status === 'pending' && <Badge tone="warn">Queued</Badge>}
            {status === 'claimed' && <Badge tone="accent">In progress</Badge>}
            <RelativeTime iso={user.createdAt} />
          </div>
          {status === 'failed' && (
            <div
              role="alert"
              className="mt-2 max-w-[85%] rounded-md border border-danger bg-danger-soft p-2 text-sm"
            >
              <p>{failReasonMessage(user.failReason)}</p>
              <Button className="mt-2" onClick={() => onResend(user.content)}>
                Resend
              </Button>
            </div>
          )}
        </div>
      )}

      <ToolActivity
        tools={turn.tools}
        interim={turn.interim}
        live={turn.reply ? [] : live?.tools}
        trace={trace}
        expandByDefault={expandTools}
      />

      {approvals.map((approval) => (
        <ApprovalCard key={approval.id} approval={approval} />
      ))}

      {turn.reply ? (
        <div className="max-w-[92%] rounded-2xl rounded-bl-sm border border-border bg-surface px-4 py-2">
          <span className="sr-only">Assistant said: </span>
          <AssistantText text={turn.reply.content} />
        </div>
      ) : inFlight ? (
        <div className="max-w-[92%] rounded-2xl rounded-bl-sm border border-border bg-surface px-4 py-2">
          <StreamingBubble
            text={live?.text ?? ''}
            since={claimedAt ?? user?.createdAt ?? new Date(serverNow()).toISOString()}
          />
        </div>
      ) : null}
    </li>
  );
}
