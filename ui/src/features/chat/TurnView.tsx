import { useEffect, useRef, useState } from 'react';
import { failReasonMessage } from '../../api/errors';
import type { LiveTurn } from '../../api/live';
import type { MessageDto, TraceLlmCallDto, TraceToolCallDto } from '../../api/types';
import { Badge, Button } from '../../components/ui';
import { AssistantText, Markdown, PlainText } from '../../lib/markdown';
import { formatTimeOfDay, serverNow, useServerNow } from '../../lib/time';
import { ApprovalCard, type ApprovalView } from '../approvals/ApprovalCard';
import { attachmentUrl, formatSize } from './attachments';
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
  const now = useServerNow(1000);
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

const LABEL = 'text-[0.7rem] tracking-wide text-muted uppercase';

/**
 * Files sent with a user message: images as thumbnails, text files as download links. Both load
 * from the gateway (same origin). An optimistic row's files have no id yet and show by name only.
 */
function MessageAttachments({
  threadId,
  attachments,
}: {
  threadId: string;
  attachments: NonNullable<MessageDto['attachments']>;
}) {
  return (
    <ul aria-label="Attached files" className="mt-1.5 flex flex-wrap gap-2">
      {attachments.map((a, i) => {
        const label = `${a.fileName} (${formatSize(Number(a.sizeBytes))})`;
        if (!a.id)
          return (
            <li key={`${a.fileName}-${i}`} className="text-xs text-muted">
              {label}
            </li>
          );
        const url = attachmentUrl(threadId, a.id);
        return (
          <li key={a.id}>
            {a.kind === 'image' ? (
              <a href={url} target="_blank" rel="noopener noreferrer" title={label}>
                <img
                  src={url}
                  alt={a.fileName}
                  loading="lazy"
                  className="max-h-40 max-w-60 rounded border border-border object-contain"
                />
              </a>
            ) : (
              <a
                href={url}
                download={a.fileName}
                className="inline-flex items-center gap-1 rounded-md border border-border bg-surface px-2 py-1 text-xs underline"
              >
                <span aria-hidden="true">📄</span>
                {label}
              </a>
            )}
          </li>
        );
      })}
    </ul>
  );
}

/** "KEN · 10:40:00": who spoke and when (local time). */
function SpeakerLabel({ name, at }: { name: string; at: string | null | undefined }) {
  return (
    <p className={LABEL}>
      {name}
      {at && <> · {formatTimeOfDay(at)}</>}
    </p>
  );
}

/**
 * One turn (3.3.1 spec section 6.3): the user's message in a raised block, then the tool rows and
 * any approval cards, then the agent's reply (or the streaming bubble) in the assistant colour.
 * The reply comes last, in the order things happened, so on a turn with many tool calls it lands
 * at the bottom where the pinned scroll is, not above rows that pushed it off screen.
 */
export function TurnView({
  turn,
  agentName,
  live,
  approvals,
  trace,
  llmCalls,
  expandTools,
  claimedAt,
  highlighted,
  threadId,
  onResend,
}: {
  turn: Turn;
  agentName: string;
  live: LiveTurn | undefined;
  approvals: ApprovalView[];
  trace: Map<string, TraceToolCallDto> | undefined;
  llmCalls?: Map<string, TraceLlmCallDto> | undefined;
  expandTools: boolean;
  claimedAt: string | null;
  highlighted: boolean;
  threadId: string;
  onResend: (message: MessageDto) => void;
}) {
  const user = turn.user;
  const status = user?.status;
  const inFlight = status === 'claimed' || (!!live && !turn.reply);

  return (
    <li className="space-y-2" id={user ? `msg-${user.id}` : undefined} data-turn={turn.correlationId}>
      {user && (
        <div>
          <SpeakerLabel
            name={user.sentByAgent ? `from ${user.sentByAgent} (agent)` : (user.sentByUser ?? 'you')}
            at={user.createdAt}
          />
          <div
            className={`mt-0.5 border-l-2 border-accent bg-surface-2 px-3 py-1.5 ${highlighted ? 'ring-2 ring-focus' : ''}`}
          >
            <span className="sr-only">
              {user.sentByAgent ? `Agent ${user.sentByAgent} said: ` : 'You said: '}
            </span>
            {user.content && <PlainText text={user.content} />}
            {user.attachments?.length ? (
              <MessageAttachments threadId={threadId} attachments={user.attachments} />
            ) : null}
          </div>
          {(status === 'pending' || status === 'claimed') && (
            <div className="mt-1 flex items-center gap-2 text-xs text-muted">
              {status === 'pending' && <Badge tone="warn">Queued</Badge>}
              {status === 'claimed' && <Badge tone="accent">In progress</Badge>}
            </div>
          )}
          {status === 'failed' && (
            <div role="alert" className="mt-2 rounded-md border border-danger bg-danger-soft p-2 text-sm">
              <p>{failReasonMessage(user.failReason)}</p>
              <Button className="mt-2" onClick={() => onResend(user)}>
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
        llmCalls={llmCalls}
        approvals={approvals}
        turnInFlight={inFlight}
        expandByDefault={expandTools}
      />

      {approvals.map((approval) => (
        <ApprovalCard key={approval.id} approval={approval} />
      ))}

      {(turn.reply || inFlight) && (
        <div>
          <SpeakerLabel name={agentName} at={turn.reply?.createdAt ?? claimedAt} />
          <div className="mt-0.5 text-assistant">
            {turn.reply ? (
              <>
                <span className="sr-only">Assistant said: </span>
                <AssistantText text={turn.reply.content} />
              </>
            ) : (
              <StreamingBubble
                text={live?.text ?? ''}
                since={claimedAt ?? user?.createdAt ?? new Date(serverNow()).toISOString()}
              />
            )}
          </div>
        </div>
      )}
    </li>
  );
}
