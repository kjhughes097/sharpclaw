import { useMutation } from '@tanstack/react-query';
import { useId, useState, type FormEvent } from 'react';
import { api, call } from '../../api/client';
import { ApiError, errorMessage } from '../../api/errors';
import { useSystem } from '../../api/hooks';
import { useAuth } from '../../app/auth';
import { useToast } from '../../app/toast';
import { Button, ErrorState, LoadingState } from '../../components/ui';
import { formatAbsolute } from '../../lib/time';

const MIN_PASSWORD = 12;

const inputClass =
  'min-h-11 w-full rounded-md border border-border bg-surface px-3 aria-[invalid=true]:border-danger';

export function AccountPage() {
  const { state, logout } = useAuth();
  const [signingOut, setSigningOut] = useState(false);
  const user = state.status === 'signed_in' ? state.user : null;
  const expiresAt = state.status === 'signed_in' ? state.expiresAt : null;

  return (
    <div className="min-h-0 flex-1 overflow-y-auto">
      <div className="mx-auto max-w-2xl space-y-6 p-4">
        <h1 className="text-xl font-semibold">Account</h1>
        <section
          aria-labelledby="account-session"
          className="space-y-3 rounded-lg border border-border bg-surface p-4"
        >
          <h2 id="account-session" className="font-semibold">
            Session
          </h2>
          <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm">
            <dt className="text-muted">Signed in as</dt>
            <dd className="break-all">{user?.name ?? 'admin key'}</dd>
            <dt className="text-muted">Session expires</dt>
            <dd>{expiresAt ? formatAbsolute(expiresAt) : '—'}</dd>
          </dl>
          <Button
            busy={signingOut}
            onClick={() => {
              setSigningOut(true);
              void logout();
            }}
          >
            Sign out
          </Button>
        </section>
        <ChangePasswordForm />
        <SystemInfo />
      </div>
    </div>
  );
}

function ChangePasswordForm() {
  const id = useId();
  const toast = useToast();
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const [errors, setErrors] = useState<{
    next?: string | null;
    confirm?: string | null;
    form?: string | null;
  }>({});

  // Passwords are the mutation's variables: `gcTime: 0` plus the reset below drop them promptly.
  const change = useMutation({
    gcTime: 0,
    mutationFn: (body: { currentPassword: string; newPassword: string }) =>
      // A wrong current password is a 400/401 for this request, not a sign-out.
      call(() => api.POST('/api/auth/password', { body }), { signOutOn401: false }),
  });

  function submit(e: FormEvent) {
    e.preventDefault();
    const found = {
      next: next.length < MIN_PASSWORD ? `Use at least ${MIN_PASSWORD} characters.` : null,
      confirm: confirm !== next ? 'The passwords do not match.' : null,
      form: current ? null : 'Enter your current password.',
    };
    setErrors(found);
    if (found.next || found.confirm || found.form) return;
    change.mutate(
      { currentPassword: current, newPassword: next },
      {
        onSuccess: () => {
          setCurrent('');
          setNext('');
          setConfirm('');
          toast('Password changed.', 'success');
        },
        onError: (err) => {
          // The server's message for a wrong password or a policy failure is written for people.
          const message =
            err instanceof ApiError && err.status === 401
              ? err.message || 'Your current password is incorrect.'
              : errorMessage(err);
          setErrors({ form: message });
        },
        onSettled: () => change.reset(),
      },
    );
  }

  const ids = { current: `${id}-current`, next: `${id}-new`, confirm: `${id}-confirm` };

  return (
    <section aria-labelledby={`${id}-title`} className="rounded-lg border border-border bg-surface p-4">
      <h2 id={`${id}-title`} className="font-semibold">
        Change password
      </h2>
      <form onSubmit={submit} noValidate className="mt-3 space-y-4">
        <div className="space-y-1">
          <label htmlFor={ids.current} className="block text-sm font-medium">
            Current password
          </label>
          <input
            id={ids.current}
            name="currentPassword"
            type="password"
            autoComplete="current-password"
            required
            value={current}
            onChange={(e) => setCurrent(e.target.value)}
            className={inputClass}
          />
        </div>
        <div className="space-y-1">
          <label htmlFor={ids.next} className="block text-sm font-medium">
            New password
          </label>
          <input
            id={ids.next}
            name="newPassword"
            type="password"
            autoComplete="new-password"
            required
            minLength={MIN_PASSWORD}
            value={next}
            onChange={(e) => setNext(e.target.value)}
            aria-describedby={`${ids.next}-hint${errors.next ? ` ${ids.next}-error` : ''}`}
            aria-invalid={errors.next ? true : undefined}
            className={inputClass}
          />
          <p id={`${ids.next}-hint`} className="text-xs text-muted">
            At least {MIN_PASSWORD} characters.
          </p>
          {errors.next && (
            <p id={`${ids.next}-error`} className="text-sm">
              <span className="font-medium">Error:</span> {errors.next}
            </p>
          )}
        </div>
        <div className="space-y-1">
          <label htmlFor={ids.confirm} className="block text-sm font-medium">
            Confirm new password
          </label>
          <input
            id={ids.confirm}
            name="confirmPassword"
            type="password"
            autoComplete="new-password"
            required
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
            aria-describedby={errors.confirm ? `${ids.confirm}-error` : undefined}
            aria-invalid={errors.confirm ? true : undefined}
            className={inputClass}
          />
          {errors.confirm && (
            <p id={`${ids.confirm}-error`} className="text-sm">
              <span className="font-medium">Error:</span> {errors.confirm}
            </p>
          )}
        </div>
        {errors.form && (
          <p role="alert" className="rounded-md bg-danger-soft p-2 text-sm">
            {errors.form}
          </p>
        )}
        <Button type="submit" variant="primary" busy={change.isPending}>
          Change password
        </Button>
      </form>
    </section>
  );
}

function SystemInfo() {
  const system = useSystem();
  return (
    <section
      aria-labelledby="account-system"
      className="space-y-3 rounded-lg border border-border bg-surface p-4"
    >
      <h2 id="account-system" className="font-semibold">
        System
      </h2>
      {system.isPending ? (
        <LoadingState label="Loading system info" />
      ) : system.isError ? (
        <ErrorState message={errorMessage(system.error)} onRetry={() => void system.refetch()} />
      ) : (
        <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm">
          <dt className="text-muted">Version</dt>
          <dd className="break-all">{system.data.version}</dd>
          <dt className="text-muted">Provider</dt>
          <dd>{system.data.provider}</dd>
          <dt className="text-muted">Server time</dt>
          <dd>{formatAbsolute(system.data.utcNow)}</dd>
        </dl>
      )}
      <p className="text-sm text-muted">You&apos;re connected over plain HTTP on your local network.</p>
    </section>
  );
}
