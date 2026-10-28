import { useQueryClient } from '@tanstack/react-query';
import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { useLocation, useNavigate } from 'react-router';
import { api, call, setCsrfToken, setUnauthorizedHandler } from '../api/client';
import type { UserDto } from '../api/types';
import { useServices } from './services';

export type AuthState =
  | { status: 'loading' }
  | { status: 'signed_out' }
  | { status: 'signed_in'; user: UserDto | null; method: string; expiresAt: string | null };

interface AuthApi {
  state: AuthState;
  login: (username: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
  /** Local sign-out: clears caches and the stream, then goes to the login page. */
  signOut: (returnTo?: string) => void;
}

const AuthContext = createContext<AuthApi | null>(null);

const PLACEHOLDER_ORIGIN = 'http://sharpclaw.invalid';

/**
 * Only a same-site relative path may be a post-login destination: it must start with a single
 * `/` (not `//` or `/\`), and contain no scheme or control characters. Anything else → `/chats`.
 */
export function safeNext(next: string | null | undefined): string {
  if (!next || typeof next !== 'string') return '/chats';
  if (!next.startsWith('/') || next.startsWith('//') || next.startsWith('/\\')) return '/chats';
  // eslint-disable-next-line no-control-regex
  if (/[\u0000-\u001f\\]/.test(next)) return '/chats';
  try {
    const url = new URL(next, PLACEHOLDER_ORIGIN);
    if (url.origin !== PLACEHOLDER_ORIGIN) return '/chats';
    if (url.pathname === '/' || url.pathname.startsWith('/login')) return '/chats';
    return url.pathname + url.search + url.hash;
  } catch {
    return '/chats';
  }
}

export function loginPath(returnTo: string): string {
  const next = safeNext(returnTo);
  return next === '/chats' ? '/login' : `/login?next=${encodeURIComponent(next)}`;
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<AuthState>({ status: 'loading' });
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const location = useLocation();
  const { stream, live, notices } = useServices();
  const locationRef = useRef(location);
  locationRef.current = location;

  const signOut = useCallback(
    (returnTo?: string) => {
      setCsrfToken(null);
      stream.stop();
      live.clearAll();
      notices.clear();
      queryClient.clear();
      const here = returnTo ?? locationRef.current.pathname + locationRef.current.search;
      // Navigate first so the auth gate doesn't add its own `next` for an explicit sign-out.
      navigate(here.startsWith('/login') ? '/login' : loginPath(here), { replace: true });
      setState({ status: 'signed_out' });
    },
    [navigate, queryClient, stream, live, notices],
  );

  useEffect(() => {
    setUnauthorizedHandler(() => signOut());
    return () => setUnauthorizedHandler(null);
  }, [signOut]);

  // Initial session check.
  useEffect(() => {
    let cancelled = false;
    call(() => api.GET('/api/auth/session'), { signOutOn401: false })
      .then((session) => {
        if (cancelled) return;
        if (session.authenticated) {
          setCsrfToken(session.csrfToken ?? null);
          setState({
            status: 'signed_in',
            user: session.user ?? null,
            method: session.method ?? 'session',
            expiresAt: session.expiresAt ?? null,
          });
        } else setState({ status: 'signed_out' });
      })
      .catch(() => {
        if (!cancelled) setState({ status: 'signed_out' });
      });
    return () => {
      cancelled = true;
    };
  }, []);

  // The one event stream per tab runs while signed in.
  useEffect(() => {
    if (state.status !== 'signed_in') return;
    stream.start();
    return () => stream.stop();
  }, [state.status, stream]);

  const login = useCallback(
    async (username: string, password: string) => {
      const result = await call(() => api.POST('/api/auth/login', { body: { username, password } }), {
        signOutOn401: false,
      });
      setCsrfToken(result.csrfToken);
      setUnauthorizedHandler(() => signOut());
      setState({ status: 'signed_in', user: result.user, method: 'session', expiresAt: result.expiresAt });
    },
    [signOut],
  );

  const logout = useCallback(async () => {
    try {
      await call(() => api.POST('/api/auth/logout'), { signOutOn401: false });
    } catch {
      // Signed out locally either way.
    }
    signOut('/login');
  }, [signOut]);

  return <AuthContext.Provider value={{ state, login, logout, signOut }}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthApi {
  const auth = useContext(AuthContext);
  if (!auth) throw new Error('AuthProvider is missing');
  return auth;
}
