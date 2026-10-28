import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react';
import { MAX_CONTENT_CHARS } from '../../api/types';
import { Button } from '../../components/ui';

/** Drafts live in memory per thread for the page's lifetime; never in browser storage. */
const drafts = new Map<string, string>();

export function clearDraftsForTests() {
  drafts.clear();
}

function finePointer(): boolean {
  return typeof window !== 'undefined' && typeof window.matchMedia === 'function'
    ? window.matchMedia('(pointer: fine)').matches
    : true;
}

export function Composer({
  threadId,
  onSend,
  busy,
  turnInFlight,
}: {
  threadId: string;
  onSend: (content: string) => Promise<boolean>;
  busy: boolean;
  turnInFlight: boolean;
}) {
  const [text, setText] = useState(() => drafts.get(threadId) ?? '');
  const ref = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    setText(drafts.get(threadId) ?? '');
  }, [threadId]);

  // Auto-grow up to a limit.
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${Math.min(el.scrollHeight, 240)}px`;
  }, [text]);

  const update = (value: string) => {
    setText(value);
    if (value) drafts.set(threadId, value);
    else drafts.delete(threadId);
  };

  const tooLong = text.length > MAX_CONTENT_CHARS;
  const empty = text.trim().length === 0;
  const disabled = empty || tooLong || busy;

  const send = async () => {
    if (disabled) return;
    if (await onSend(text)) update('');
  };

  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key !== 'Enter' || e.nativeEvent.isComposing) return;
    // Ctrl/Cmd+Enter always sends; plain Enter sends only with a fine pointer (not on touch).
    if (e.ctrlKey || e.metaKey || (!e.shiftKey && finePointer())) {
      e.preventDefault();
      void send();
    }
  };

  const nearLimit = text.length > MAX_CONTENT_CHARS * 0.9;

  return (
    <form
      className="border-t border-border bg-surface p-2"
      onSubmit={(e) => {
        e.preventDefault();
        void send();
      }}
    >
      {turnInFlight && !empty && (
        <p className="px-1 pb-1 text-xs text-muted">The agent is busy; your message will queue.</p>
      )}
      <div className="flex items-end gap-2">
        <label htmlFor={`composer-${threadId}`} className="sr-only">
          Message
        </label>
        <textarea
          id={`composer-${threadId}`}
          ref={ref}
          rows={1}
          value={text}
          onChange={(e) => update(e.target.value)}
          onKeyDown={onKeyDown}
          placeholder="Message the agent"
          aria-describedby={nearLimit ? `composer-count-${threadId}` : undefined}
          className="max-h-60 min-h-11 flex-1 resize-none rounded-md border border-border bg-surface px-3 py-2 text-base"
        />
        <Button type="submit" variant="primary" disabled={disabled} busy={busy}>
          Send
        </Button>
      </div>
      {nearLimit && (
        <p
          id={`composer-count-${threadId}`}
          className={`px-1 pt-1 text-xs ${tooLong ? 'text-danger' : 'text-muted'}`}
        >
          {text.length.toLocaleString()} / {MAX_CONTENT_CHARS.toLocaleString()} characters
        </p>
      )}
    </form>
  );
}
