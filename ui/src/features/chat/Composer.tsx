import {
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  type ClipboardEvent,
  type DragEvent,
  type KeyboardEvent,
} from 'react';
import { MAX_CONTENT_CHARS } from '../../api/types';
import { Button } from '../../components/ui';
import {
  ACCEPT,
  MAX_ATTACHMENTS,
  checkFile,
  formatSize,
  kindOf,
  namePasted,
  readAttachment,
  type PendingAttachment,
} from './attachments';

/** What the composer sends: the text and any attached files (base64). */
export interface OutgoingMessage {
  content: string;
  attachments: { fileName: string; data: string }[];
}

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
  onSend: (message: OutgoingMessage) => Promise<boolean>;
  busy: boolean;
  turnInFlight: boolean;
}) {
  const [text, setText] = useState(() => drafts.get(threadId) ?? '');
  // Attachments stay with the open thread only; switching threads drops them.
  const [files, setFiles] = useState<PendingAttachment[]>([]);
  const [fileErrors, setFileErrors] = useState<string[]>([]);
  const [dragging, setDragging] = useState(false);
  const ref = useRef<HTMLTextAreaElement>(null);
  const picker = useRef<HTMLInputElement>(null);

  useEffect(() => {
    setText(drafts.get(threadId) ?? '');
    setFiles([]);
    setFileErrors([]);
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

  /** Checks and reads files, keeping the ones that pass and explaining the ones that don't. */
  const addFiles = async (incoming: File[]) => {
    if (incoming.length === 0) return;
    const errors: string[] = [];
    const accepted: PendingAttachment[] = [];
    let room = MAX_ATTACHMENTS - files.length;
    for (const file of incoming) {
      const problem = checkFile(file);
      if (problem) {
        errors.push(problem);
        continue;
      }
      if (room <= 0) {
        errors.push(`${file.name}: at most ${MAX_ATTACHMENTS} files per message.`);
        continue;
      }
      try {
        accepted.push(await readAttachment(file, kindOf(file.name)!));
        room--;
      } catch {
        errors.push(`${file.name} couldn't be read.`);
      }
    }
    setFileErrors(errors);
    if (accepted.length) setFiles((current) => [...current, ...accepted]);
  };

  const remove = (key: string) => setFiles((current) => current.filter((f) => f.key !== key));

  const onPaste = (e: ClipboardEvent<HTMLTextAreaElement>) => {
    // Pasted images become attachments; pasted text is left to the textarea.
    const images = [...e.clipboardData.files].filter((f) => f.type.startsWith('image/'));
    if (images.length === 0) return;
    e.preventDefault();
    void addFiles(images.map((f, i) => namePasted(f, i)));
  };

  const onDrop = (e: DragEvent<HTMLFormElement>) => {
    e.preventDefault();
    setDragging(false);
    void addFiles([...e.dataTransfer.files]);
  };

  const tooLong = text.length > MAX_CONTENT_CHARS;
  const empty = text.trim().length === 0 && files.length === 0;
  const disabled = empty || tooLong || busy;

  const send = async () => {
    if (disabled) return;
    const attachments = files.map((f) => ({ fileName: f.fileName, data: f.data }));
    if (await onSend({ content: text, attachments })) {
      update('');
      setFiles([]);
      setFileErrors([]);
    }
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
      className={`border-t border-border bg-surface p-2 ${dragging ? 'ring-2 ring-focus' : ''}`}
      onSubmit={(e) => {
        e.preventDefault();
        void send();
      }}
      onDragOver={(e) => {
        if (![...e.dataTransfer.types].includes('Files')) return;
        e.preventDefault();
        setDragging(true);
      }}
      onDragLeave={() => setDragging(false)}
      onDrop={onDrop}
    >
      {turnInFlight && !empty && (
        <p className="px-1 pb-1 text-xs text-muted">The agent is busy; your message will queue.</p>
      )}
      {fileErrors.length > 0 && (
        <ul role="alert" className="px-1 pb-1 text-xs text-danger">
          {fileErrors.map((error) => (
            <li key={error}>{error}</li>
          ))}
        </ul>
      )}
      {files.length > 0 && (
        <ul aria-label="Attachments" className="flex flex-wrap gap-2 px-1 pb-2">
          {files.map((f) => (
            <li
              key={f.key}
              className="flex items-center gap-2 rounded-md border border-border bg-surface-2 py-1 pr-1 pl-2 text-xs"
            >
              {f.previewUrl ? (
                <img src={f.previewUrl} alt="" className="h-8 w-8 rounded object-cover" />
              ) : (
                <span aria-hidden="true">📄</span>
              )}
              <span className="max-w-40 truncate" title={f.fileName}>
                {f.fileName}
              </span>
              <span className="text-muted">{formatSize(f.size)}</span>
              <button
                type="button"
                onClick={() => remove(f.key)}
                aria-label={`Remove ${f.fileName}`}
                className="min-h-8 min-w-8 rounded hover:bg-surface"
              >
                ×
              </button>
            </li>
          ))}
        </ul>
      )}
      <div className="flex items-end gap-2">
        <input
          ref={picker}
          type="file"
          multiple
          accept={ACCEPT}
          className="sr-only"
          tabIndex={-1}
          aria-hidden="true"
          data-testid="attachment-input"
          onChange={(e) => {
            void addFiles([...(e.target.files ?? [])]);
            e.target.value = ''; // so picking the same file again still fires
          }}
        />
        <Button
          variant="ghost"
          aria-label="Attach files"
          title="Attach files (images or text; you can also drop or paste them)"
          onClick={() => picker.current?.click()}
          disabled={files.length >= MAX_ATTACHMENTS}
          className="px-3"
        >
          📎
        </Button>
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
          onPaste={onPaste}
          placeholder="Message the agent (Enter sends, Shift+Enter newline; markdown supported)"
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
