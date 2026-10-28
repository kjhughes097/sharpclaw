import { DropdownMenu } from 'radix-ui';
import { useState } from 'react';
import { Link } from 'react-router';
import type { ThreadDetailResponse } from '../../api/types';
import { Meter } from '../../components/ui';
import { formatTokens } from '../../lib/time';
import { useUpdateThread } from './useUpdateThread';

type Detail = ThreadDetailResponse;

const MENU_ITEM =
  'flex min-h-11 cursor-pointer items-center rounded px-3 text-sm outline-none data-[highlighted]:bg-surface-2';

/** Title (click to rename), agent and model, the context meter, and the chat menu. */
export function ThreadHeader({
  threadId,
  thread,
  agent,
  backLink,
  onOpenTrace,
  expandTools,
  onExpandToolsChange,
}: {
  threadId: string;
  thread: Detail['thread'];
  agent: Detail['agent'];
  backLink?: boolean;
  onOpenTrace: () => void;
  expandTools: boolean;
  onExpandToolsChange: (expand: boolean) => void;
}) {
  return (
    <header className="flex flex-wrap items-center gap-2 border-b border-border bg-surface px-3 py-1">
      {backLink && (
        <Link
          to="/chats"
          className="inline-flex min-h-11 min-w-11 items-center justify-center rounded-md hover:bg-surface-2 md:hidden"
          aria-label="Back to chats"
        >
          ←
        </Link>
      )}
      <div className="flex min-w-0 flex-1 flex-col">
        <TitleEditor key={thread.title ?? ''} threadId={threadId} title={thread.title ?? null} />
        <p className="truncate px-1 text-xs text-muted">
          {agent.name} · {agent.model}
          {thread.archived && ' · archived'}
        </p>
      </div>
      <ContextIndicator context={thread.context} />
      <DropdownMenu.Root modal={false}>
        <DropdownMenu.Trigger
          className="inline-flex min-h-11 min-w-11 items-center justify-center rounded-md hover:bg-surface-2"
          aria-label="Chat menu"
        >
          ⋯
        </DropdownMenu.Trigger>
        <DropdownMenu.Portal>
          <DropdownMenu.Content
            align="end"
            className="z-30 min-w-56 rounded-md border border-border bg-surface p-1 shadow-lg"
          >
            <DropdownMenu.Item className={MENU_ITEM} onSelect={onOpenTrace}>
              Trace
            </DropdownMenu.Item>
            <ArchiveItem threadId={threadId} archived={thread.archived ?? false} />
            <DropdownMenu.CheckboxItem
              className={`${MENU_ITEM} gap-2`}
              checked={expandTools}
              onCheckedChange={onExpandToolsChange}
            >
              <span aria-hidden="true">{expandTools ? '☑' : '☐'}</span> Expand tool details by default
            </DropdownMenu.CheckboxItem>
          </DropdownMenu.Content>
        </DropdownMenu.Portal>
      </DropdownMenu.Root>
    </header>
  );
}

function ContextIndicator({ context }: { context: Detail['thread']['context'] }) {
  if (!context) return null;
  const label = `~${formatTokens(context.estimatedTokens)} / ${formatTokens(context.windowTokens)} tokens`;
  return (
    <div className="w-36" title="Approximate: from the last time the gateway built this thread's context">
      <p className="text-xs text-muted">
        {label}
        <span className="sr-only"> (approximate)</span>
      </p>
      <Meter
        value={context.estimatedTokens}
        max={context.windowTokens}
        label="Context window used (approximate)"
      />
    </div>
  );
}

function TitleEditor({ threadId, title }: { threadId: string; title: string | null }) {
  const [editing, setEditing] = useState(false);
  const [value, setValue] = useState(title ?? '');
  const rename = useUpdateThread(threadId);

  if (!editing)
    return (
      <button
        type="button"
        className="min-h-11 min-w-0 truncate rounded px-1 text-left text-base font-semibold hover:bg-surface-2"
        onClick={() => {
          setValue(title ?? '');
          setEditing(true);
        }}
        title="Rename"
      >
        {title || 'Untitled chat'}
        <span className="sr-only"> (rename)</span>
      </button>
    );

  const save = () => {
    const trimmed = value.trim();
    setEditing(false);
    if (trimmed && trimmed !== title && trimmed.length <= 120)
      rename.mutate({ title: trimmed, archived: null });
  };
  return (
    <input
      aria-label="Chat title"
      // Focus moves into the field the user just asked to edit.
      // eslint-disable-next-line jsx-a11y/no-autofocus
      autoFocus
      maxLength={120}
      value={value}
      onChange={(e) => setValue(e.target.value)}
      onBlur={save}
      onKeyDown={(e) => {
        if (e.key === 'Enter') save();
        if (e.key === 'Escape') setEditing(false);
      }}
      className="min-h-11 min-w-0 flex-1 rounded-md border border-border bg-surface px-2 font-semibold"
    />
  );
}

function ArchiveItem({ threadId, archived }: { threadId: string; archived: boolean }) {
  const mutation = useUpdateThread(threadId, (nowArchived) =>
    nowArchived ? 'Chat archived.' : 'Chat restored.',
  );
  return (
    <DropdownMenu.Item
      className={MENU_ITEM}
      onSelect={() => mutation.mutate({ title: null, archived: !archived })}
    >
      {archived ? 'Unarchive' : 'Archive'}
    </DropdownMenu.Item>
  );
}
