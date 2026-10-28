import type { ReactNode } from 'react';
import { Badge, RelativeTime } from '../../components/ui';
import { serverNow } from '../../lib/time';

/** An on/off switch with a visible state label (never colour alone). */
export function EnabledSwitch({
  checked,
  label,
  disabled,
  onChange,
}: {
  checked: boolean;
  label: string;
  disabled?: boolean;
  onChange: (next: boolean) => void;
}) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      disabled={disabled}
      onClick={() => onChange(!checked)}
      className="inline-flex min-h-11 items-center gap-2 rounded-md px-1 text-sm disabled:cursor-not-allowed disabled:opacity-50"
    >
      <span
        aria-hidden="true"
        className={`relative inline-block h-6 w-10 rounded-full border border-border transition-colors ${checked ? 'bg-accent' : 'bg-surface-2'}`}
      >
        <span
          className={`absolute top-0.5 h-4.5 w-4.5 rounded-full bg-surface shadow transition-transform ${checked ? 'translate-x-4.5' : 'translate-x-0.5'}`}
        />
      </span>
      <span aria-hidden="true">{checked ? 'Enabled' : 'Disabled'}</span>
    </button>
  );
}

const STALE_MS = 3 * 60_000;

/** Last seen: "never", a relative time, or a warning badge when older than 3 minutes. */
export function LastSeen({ iso }: { iso: string | null | undefined }) {
  if (!iso) return <Badge tone="neutral">never</Badge>;
  const stale = serverNow() - Date.parse(iso) > STALE_MS;
  if (!stale) return <RelativeTime iso={iso} />;
  return (
    <Badge tone="warn" title="Not seen in the last 3 minutes">
      <span>
        Not seen recently: <RelativeTime iso={iso} />
      </span>
    </Badge>
  );
}

export function ConfigNote() {
  return (
    <p className="text-sm text-muted">
      Persona and tool policy are managed in the agent&apos;s config files.
    </p>
  );
}

/** A labelled form field with an optional hint and error, wired up for screen readers. */
export function Field({
  id,
  label,
  hint,
  error,
  children,
}: {
  id: string;
  label: string;
  hint?: string;
  error?: string | null;
  children: ReactNode;
}) {
  return (
    <div className="space-y-1">
      <label htmlFor={id} className="block text-sm font-medium">
        {label}
      </label>
      {children}
      {hint && (
        <p id={`${id}-hint`} className="text-xs text-muted">
          {hint}
        </p>
      )}
      {error && (
        <p id={`${id}-error`} className="text-sm text-fg">
          <span className="font-medium">Error:</span> {error}
        </p>
      )}
    </div>
  );
}

/** `aria-describedby`/`aria-invalid` for an input inside {@link Field}. */
export function describedBy(id: string, hint: boolean, error: string | null | undefined) {
  const ids = [hint ? `${id}-hint` : null, error ? `${id}-error` : null].filter(Boolean).join(' ');
  return { 'aria-describedby': ids || undefined, 'aria-invalid': error ? true : undefined };
}
