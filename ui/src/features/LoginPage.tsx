import { useState, type FormEvent } from 'react';
import { Navigate, useNavigate, useSearchParams } from 'react-router';
import { ApiError, messageForCode } from '../api/errors';
import { safeNext, useAuth } from '../app/auth';
import { Button } from '../components/ui';

export function LoginPage() {
  const { state, login } = useAuth();
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const next = safeNext(params.get('next'));

  if (state.status === 'signed_in') return <Navigate to={next} replace />;

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await login(username, password);
      navigate(next, { replace: true });
    } catch (err) {
      if (err instanceof ApiError && err.code === 'rate_limited')
        setError(messageForCode('rate_limited', { retryAfter: err.retryAfterSeconds }));
      else if (err instanceof ApiError && err.status === 401)
        // The server's message is deliberately generic.
        setError('Invalid username or password.');
      else if (err instanceof ApiError && err.code === 'csrf_failed')
        setError('The sign-in request was rejected. Reload the page and try again.');
      else setError('Could not reach the gateway. Try again.');
    } finally {
      setBusy(false);
      setPassword('');
    }
  }

  return (
    <main id="main" className="app-height flex items-center justify-center p-4">
      <form onSubmit={submit} className="w-full max-w-sm space-y-4 rounded-xl border border-border bg-surface p-6 shadow-sm">
        <h1 className="text-xl font-semibold">Sign in to SharpClaw</h1>
        <div className="space-y-1">
          <label htmlFor="username" className="block text-sm font-medium">
            Username
          </label>
          <input
            id="username"
            name="username"
            autoComplete="username"
            autoCapitalize="none"
            spellCheck={false}
            required
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            className="min-h-11 w-full rounded-md border border-border bg-surface px-3"
          />
        </div>
        <div className="space-y-1">
          <label htmlFor="password" className="block text-sm font-medium">
            Password
          </label>
          <input
            id="password"
            name="password"
            type="password"
            autoComplete="current-password"
            required
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            className="min-h-11 w-full rounded-md border border-border bg-surface px-3"
          />
        </div>
        {error && (
          <p role="alert" className="rounded-md bg-danger-soft p-2 text-sm">
            {error}
          </p>
        )}
        <Button type="submit" variant="primary" busy={busy} className="w-full">
          Sign in
        </Button>
        <p className="text-xs text-muted">You&apos;re connected over plain HTTP on your local network.</p>
      </form>
    </main>
  );
}
