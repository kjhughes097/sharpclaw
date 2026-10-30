import { Dialog as RadixDialog } from 'radix-ui';
import { type ReactNode } from 'react';
import { Button } from './Button';

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
          className="fixed top-1/2 left-1/2 z-50 max-h-[90dvh] w-[min(94vw,32rem)] -translate-x-1/2 -translate-y-1/2 overflow-y-auto rounded-xl border border-border bg-surface p-5 shadow-xl lcars:rounded-l-3xl lcars:rounded-r-none lcars:border-0 lcars:border-l-16 lcars:border-accent"
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
          <RadixDialog.Title className="text-lg font-semibold lcars:text-2xl lcars:font-normal lcars:text-accent lcars:uppercase">
            {title}
          </RadixDialog.Title>
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
