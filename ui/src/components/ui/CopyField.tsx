import { useRef, useState } from 'react';
import { copyText, selectContents } from '../../lib/clipboard';
import { Button } from './Button';

/** A value shown selectable, with a copy button that uses the non-secure-context fallback. */
export function CopyField({ value, label }: { value: string; label: string }) {
  const ref = useRef<HTMLElement>(null);
  const [status, setStatus] = useState<'idle' | 'copied' | 'failed'>('idle');
  return (
    <div>
      <div className="flex items-stretch gap-2">
        <code
          ref={ref}
          aria-label={label}
          className="block min-w-0 flex-1 overflow-x-auto rounded-md border border-border bg-surface-2 px-3 py-2 font-mono text-sm break-all select-all"
        >
          {value}
        </code>
        <Button
          onClick={() => {
            const ok = copyText(value);
            setStatus(ok ? 'copied' : 'failed');
            if (!ok) selectContents(ref.current);
          }}
        >
          Copy
        </Button>
      </div>
      <p className="mt-1 min-h-5 text-xs text-muted" aria-live="polite">
        {status === 'copied'
          ? 'Copied.'
          : status === 'failed'
            ? 'Copy failed — the text is selected; copy it by hand.'
            : ''}
      </p>
    </div>
  );
}
