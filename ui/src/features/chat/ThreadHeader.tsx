import { DropdownMenu } from 'radix-ui';
import { useState } from 'react';
import type { ThreadDetailResponse } from '../../api/types';
import { useUpdateThread } from './useUpdateThread';

type Detail = ThreadDetailResponse;

const MENU_ITEM =
  'flex min-h-11 cursor-pointer items-center rounded px-3 text-sm outline-none data-[highlighted]:bg-surface-2';

/**
 * The thread's title, small and muted (click to rename), with a menu: Rename, Trace, Archive or
 * Unarchive, and the tool-details preference. Agent, model and context live in the status bar.
 */
export function ThreadHeader({
  threadId,
  thread,
  onOpenTrace,
  expandTools,
  onExpandToolsChange,
}: {
  threadId: string;
  thread: Detail['thread'];
  onOpenTrace: () => void;
  expandTools: boolean;
  onExpandToolsChange: (expand: boolean) => void;
}) {
  const [editing, setEditing] = useState(false);
  return (
    <header className="flex items-center gap-1 px-3 pt-1">
      <TitleEditor
        key={thread.title ?? ''}
        threadId={threadId}
        title={thread.title ?? null}
        editing={editing}
        onEditingChange={setEditing}
      />
      {thread.archived && <span className="text-xs text-muted">· archived</span>}
      <DropdownMenu.Root modal={false}>
        <DropdownMenu.Trigger
          className="inline-flex min-h-8 min-w-8 items-center justify-center rounded-md text-muted hover:bg-surface-2"
          aria-label="Chat menu"
        >
          ⋯
        </DropdownMenu.Trigger>
        <DropdownMenu.Portal>
          <DropdownMenu.Content
            align="start"
            className="z-30 min-w-56 rounded-md border border-border bg-surface p-1 shadow-lg"
          >
            <DropdownMenu.Item className={MENU_ITEM} onSelect={() => setEditing(true)}>
              Rename
            </DropdownMenu.Item>
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

function TitleEditor({
  threadId,
  title,
  editing,
  onEditingChange: setEditing,
}: {
  threadId: string;
  title: string | null;
  editing: boolean;
  onEditingChange: (editing: boolean) => void;
}) {
  const [value, setValue] = useState(title ?? '');
  const rename = useUpdateThread(threadId);

  if (!editing)
    return (
      <button
        type="button"
        className="min-h-8 min-w-0 truncate rounded px-1 text-left text-xs text-muted hover:bg-surface-2"
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
    if (trimmed && trimmed !== title && trimmed.length <= 120) rename.mutate({ title: trimmed });
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
      className="min-h-8 max-w-md min-w-0 flex-1 rounded-md border border-border bg-surface px-2 text-sm"
    />
  );
}

function ArchiveItem({ threadId, archived }: { threadId: string; archived: boolean }) {
  const mutation = useUpdateThread(threadId, (nowArchived) =>
    nowArchived ? 'Chat archived.' : 'Chat restored.',
  );
  return (
    <DropdownMenu.Item className={MENU_ITEM} onSelect={() => mutation.mutate({ archived: !archived })}>
      {archived ? 'Unarchive' : 'Archive'}
    </DropdownMenu.Item>
  );
}
