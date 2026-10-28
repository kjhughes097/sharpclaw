import { type ReactNode } from 'react';
import { Button } from './Button';

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
