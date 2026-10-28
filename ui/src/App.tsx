import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Tooltip } from 'radix-ui';
import type { ReactNode } from 'react';
import { Navigate, Route, Routes, useLocation, useParams } from 'react-router';
import { AuthProvider, loginPath, useAuth } from './app/auth';
import { LiveEvents } from './app/LiveEvents';
import { ServicesProvider, type Services } from './app/services';
import { ToastProvider } from './app/toast';
import { AppShell } from './components/AppShell';
import { LoadingState } from './components/ui';
import { AccountPage } from './features/account/AccountPage';
import { AgentTabPage } from './features/agents/AgentTabPage';
import { AgentsPage } from './features/agents/AgentsPage';
import { ApprovalsPage } from './features/approvals/ApprovalsPage';
import { AuditPage } from './features/audit/AuditPage';
import { LoginPage } from './features/LoginPage';
import { NotFoundPage } from './features/NotFoundPage';
import { OverviewPage } from './features/overview/OverviewPage';
import { PromptsPage } from './features/prompts/PromptsPage';

export function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        // Events invalidate what changed; refetching on every focus would only add load.
        refetchOnWindowFocus: false,
        retry: (count, error) =>
          count < 2 &&
          !(error instanceof Error && 'status' in error && (error as { status: number }).status < 500),
        staleTime: 15_000,
      },
      mutations: { retry: false },
    },
  });
}

/** 3.3 bookmarks: `/chats[/:threadId]` → `/prompts[/:threadId]`, keeping the query string. */
function LegacyChatsRedirect() {
  const { threadId } = useParams();
  const { search } = useLocation();
  return <Navigate to={`/prompts${threadId ? `/${threadId}` : ''}${search}`} replace />;
}

/** Unauthenticated → `/login?next=<path>` (3.3 spec section 9.1). */
function RequireAuth({ children }: { children: ReactNode }) {
  const { state } = useAuth();
  const location = useLocation();
  if (state.status === 'loading') return <LoadingState label="Checking your session" />;
  if (state.status === 'signed_out')
    return <Navigate to={state.redirect ?? loginPath(location.pathname + location.search)} replace />;
  return (
    <>
      <LiveEvents />
      {children}
    </>
  );
}

export function AppRoutes() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route
        element={
          <RequireAuth>
            <AppShell />
          </RequireAuth>
        }
      >
        <Route index element={<Navigate to="/overview" replace />} />
        <Route path="/overview" element={<OverviewPage />} />
        <Route path="/prompts" element={<PromptsPage />} />
        <Route path="/prompts/:threadId" element={<PromptsPage />} />
        <Route path="/chats" element={<LegacyChatsRedirect />} />
        <Route path="/chats/:threadId" element={<LegacyChatsRedirect />} />
        <Route path="/approvals" element={<ApprovalsPage />} />
        <Route path="/agents" element={<AgentsPage />} />
        <Route path="/agents/:name" element={<AgentTabPage />} />
        <Route path="/audit" element={<AuditPage />} />
        <Route path="/account" element={<AccountPage />} />
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  );
}

/** Providers shared by the app and the tests (which supply their own router and services). */
export function AppProviders({
  queryClient,
  services,
  children,
}: {
  queryClient: QueryClient;
  services: Services;
  children: ReactNode;
}) {
  return (
    <QueryClientProvider client={queryClient}>
      <ServicesProvider services={services}>
        <ToastProvider>
          <Tooltip.Provider delayDuration={300}>
            <AuthProvider>{children}</AuthProvider>
          </Tooltip.Provider>
        </ToastProvider>
      </ServicesProvider>
    </QueryClientProvider>
  );
}
