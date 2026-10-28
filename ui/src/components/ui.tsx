import { Dialog as RadixDialog } from 'radix-ui';
import { forwardRef, useRef, useState, type ButtonHTMLAttributes, type ReactNode } from 'react';
import { copyText, selectContents } from '../lib/clipboard';
import { formatAbsolute, formatRelative, useServerNow } from '../lib/time';

type Variant = 'primary' | 'secondary' | 'danger' | 'ghost';

const VARIANTS: Record<Variant, string> = {
  primary: 'bg-accent text-accent-fg hover:opacity-90',
  secondary: 'border border-border bg-surface text-fg hover:bg-surface-2',
  danger: 'bg-danger text-surface hover:opacity-90',
  ghost: 'text-fg hover:bg-surface-2',
};

/** Touch targets are at least 44 px tall (3.3 spec section 9.10). */
export const Button = forwardRef<
  HTMLButtonElement,
  ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant; busy?: boolean }
>(function Button({ variant = 'secondary', busy = false, className = '', children, disabled, ...rest }, ref) {
  return (
    <button
      ref={ref}
      type="button"
      className={`inline-flex min-h-11 items-center justify-center gap-2 rounded-md px-4 text-sm font-medium disabled:cursor-not-allowed disabled:opacity-50 ${VARIANTS[variant]} ${className}`}
      disabled={disabled || busy}
      aria-busy={busy || undefined}
      {...rest}
    >
      {busy && <Spinner small />}
      {children}
    </button>
  );
});

export function Spinner({ small = false, label }: { small?: boolean; label?: string }) {
  return (
    <span
      role={label ? 'status' : undefined}
      aria-label={label}
      className={`inline-block animate-spin rounded-full border-2 border-current border-t-transparent ${small ? 'h-3.5 w-3.5' : 'h-5 w-5'}`}
    />
  );
}

export function Skeleton({ lines = 3 }: { lines?: number }) {
  return (
    <div className="space-y-2 p-4" aria-hidden="true">
      {Array.from({ length: lines }, (_, i) => (
        <div key={i} className="h-4 animate-pulse rounded bg-surface-2" />
      ))}
    </div>
  );
}

export function LoadingState({ label = 'Loading' }: { label?: string }) {
  return (
    <div role="status" aria-live="polite">
      <span className="sr-only">{label}…</span>
      <Skeleton />
    </div>
  );
}

export function EmptyState({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="m-4 rounded-lg border border-dashed border-border p-6 text-center">
      <p className="font-medium">{title}</p>
      {children && <div className="mt-1 text-sm text-muted">{children}</div>}
    </div>
  );
}

export function ErrorState({ message, onRetry }: { message: string; onRetry?: () => void }) {
  return (
    <div role="alert" className="m-4 rounded-lg border border-danger bg-danger-soft p-4 text-sm">
      <p>{message}</p>
      {onRetry && (
        <Button className="mt-2" onClick={onRetry}>
          Retry
        </Button>
      )}
    </div>
  );
}

export function Badge({
  children,
  tone = 'neutral',
  title,
}: {
  children: ReactNode;
  tone?: 'neutral' | 'accent' | 'warn' | 'danger' | 'ok';
  title?: string;
}) {
  const tones = {
    neutral: 'bg-surface-2 text-fg',
    accent: 'bg-accent-soft text-fg',
    warn: 'bg-warn-soft text-fg',
    danger: 'bg-danger-soft text-fg',
    ok: 'bg-ok-soft text-fg',
  };
  return (
    <span
      title={title}
      className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${tones[tone]}`}
    >
      {children}
    </span>
  );
}

export function RiskBadge({ risk }: { risk: string | null | undefined }) {
  if (!risk) return null;
  const tone = risk === 'critical' || risk === 'high' ? 'danger' : risk === 'medium' ? 'warn' : 'neutral';
  return <Badge tone={tone}>risk: {risk}</Badge>;
}

/** A relative time with the absolute time available without hover (title + visually hidden text). */
export function RelativeTime({ iso }: { iso: string | null | undefined }) {
  const now = useServerNow();
  if (!iso) return <span>—</span>;
  const absolute = formatAbsolute(iso);
  return (
    <time dateTime={iso} title={absolute}>
      {formatRelative(iso, now)}
      <span className="sr-only"> ({absolute})</span>
    </time>
  );
}

export function Dialog({
  open,
  onOpenChange,
  title,
  description,
  children,
  dismissible = true,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description?: string;
  children: ReactNode;
  /** False for dialogs that must be closed with an explicit action (the token reveal). */
  dismissible?: boolean;
}) {
  return (
    <RadixDialog.Root open={open} onOpenChange={onOpenChange}>
      <RadixDialog.Portal>
        <RadixDialog.Overlay className="fixed inset-0 z-40 bg-black/50" />
        <RadixDialog.Content
          className="fixed top-1/2 left-1/2 z-50 max-h-[90dvh] w-[min(94vw,32rem)] -translate-x-1/2 -translate-y-1/2 overflow-y-auto rounded-xl border border-border bg-surface p-5 shadow-xl"
          onEscapeKeyDown={(e) => {
            if (!dismissible) e.preventDefault();
          }}
          onPointerDownOutside={(e) => {
            if (!dismissible) e.preventDefault();
          }}
          onInteractOutside={(e) => {
            if (!dismissible) e.preventDefault();
          }}
        >
          <RadixDialog.Title className="text-lg font-semibold">{title}</RadixDialog.Title>
          {description ? (
            <RadixDialog.Description className="mt-1 text-sm text-muted">
              {description}
            </RadixDialog.Description>
          ) : (
            <RadixDialog.Description className="sr-only">{title}</RadixDialog.Description>
          )}
          <div className="mt-4">{children}</div>
        </RadixDialog.Content>
      </RadixDialog.Portal>
    </RadixDialog.Root>
  );
}

export function ConfirmDialog({
  open,
  onOpenChange,
  title,
  description,
  confirmLabel,
  onConfirm,
  danger = false,
  busy = false,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description: string;
  confirmLabel: string;
  onConfirm: () => void;
  danger?: boolean;
  busy?: boolean;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange} title={title} description={description}>
      <div className="flex justify-end gap-2">
        <Button onClick={() => onOpenChange(false)}>Cancel</Button>
        <Button variant={danger ? 'danger' : 'primary'} busy={busy} onClick={onConfirm}>
          {confirmLabel}
        </Button>
      </div>
    </Dialog>
  );
}

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

export function Meter({ value, max, label }: { value: number; max: number; label: string }) {
  const pct = max > 0 ? Math.min(100, Math.round((value / max) * 100)) : 0;
  return (
    <div
      role="meter"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={max}
      aria-valuenow={Math.min(value, max)}
      className="h-1.5 w-full overflow-hidden rounded-full bg-surface-2"
    >
      {/* React applies `style` through the CSSOM, which the CSP allows (unlike style attributes in markup). */}
      <div
        className={`h-full ${pct >= 90 ? 'bg-danger' : pct >= 70 ? 'bg-warn' : 'bg-accent'}`}
        style={{ width: `${pct}%` }}
      />
    </div>
  );
}
