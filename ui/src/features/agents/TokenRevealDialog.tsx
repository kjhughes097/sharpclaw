import { useId, useState } from 'react';
import { Button, CopyField, Dialog } from '../../components/ui';

/**
 * Shows a new agent token once. The value lives only in the parent's state while this is open;
 * the parent clears it in `onClose`. It cannot be dismissed by Esc or an outside click, and closes
 * only after the user confirms they saved it (3.3 spec section 9.7).
 */
export function TokenRevealDialog({ value, onClose }: { value: string | null; onClose: () => void }) {
  const [saved, setSaved] = useState(false);
  const checkboxId = useId();

  function close() {
    setSaved(false);
    onClose();
  }

  return (
    <Dialog
      open={value !== null}
      onOpenChange={(open) => {
        if (!open && saved) close();
      }}
      title="Agent token"
      description="Give this token to the agent's container."
      dismissible={false}
    >
      {value !== null && (
        <div className="space-y-4">
          <p role="alert" className="rounded-md border border-warn bg-warn-soft p-3 text-sm">
            This token will not be shown again. Store it now — the agent needs it as AGENT_TOKEN.
          </p>
          <CopyField value={value} label="Agent token" />
          <div className="flex min-h-11 items-center gap-3">
            <input
              id={checkboxId}
              type="checkbox"
              checked={saved}
              onChange={(e) => setSaved(e.target.checked)}
              className="h-5 w-5"
            />
            <label htmlFor={checkboxId} className="text-sm">
              I&apos;ve saved it
            </label>
          </div>
          <div className="flex justify-end">
            <Button variant="primary" disabled={!saved} onClick={close}>
              Close
            </Button>
          </div>
        </div>
      )}
    </Dialog>
  );
}
