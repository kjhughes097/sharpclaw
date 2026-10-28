import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Tooltip } from 'radix-ui';
import type { ReactNode } from 'react';
import { Navigate, Route, Routes, useLocation } from 'react-router';
import { AuthProvider, loginPath, useAuth } from './app/auth';
import { LiveEvents } from './app/LiveEvents';
import { ServicesProvider, type Services } from './app/services';
import { ToastProvider } from './app/toast';
import { AppShell } from './components/AppShell';
import { LoadingState } from './components/ui';
import { AccountPage } from './features/account/AccountPage';
import { AgentDetailPage } from './features/agents/AgentDetailPage';
import { AgentsPage } from './features/agents/AgentsPage';
import { ApprovalsPage } from './features/approvals/ApprovalsPage';
import { AuditPage } from './features/audit/AuditPage';
import { ChatsPage } from './features/chat/ChatsPage';
import { LoginPage } from './features/LoginPage';
import { NotFoundPage } from './features/NotFoundPage';

export function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        // Events invalidate what changed; refetching on every focus would only add load.
        refetchOnWindowFocus: false,
        retry: (count, error) => count < 2 && !(error instanceof Error && 'status' in error && (error as { status: number }).status < 500),
        staleTime: 15_000,
      },
      mutations: { retry: false },
    },
  });
}

/** Unauthenticated → `/login?next=<path>` (3.3 spec section 9.1). */
function RequireAuth({ children }: { children: ReactNode }) {
  const { state } = useAuth();
  const location = useLocation();
  if (state.status === 'loading') return <LoadingState label="Checking your session" />;
  if (state.status === 'signed_out') return <Navigate to={loginPath(location.pathname + location.search)} replace />;
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
        <Route index element={<Navigate to="/chats" replace />} />
        <Route path="/chats" element={<ChatsPage />} />
        <Route path="/chats/:threadId" element={<ChatsPage />} />
        <Route path="/approvals" element={<ApprovalsPage />} />
        <Route path="/agents" element={<AgentsPage />} />
        <Route path="/agents/:name" element={<AgentDetailPage />} />
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
