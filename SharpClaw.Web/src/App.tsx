import { useCallback, useEffect, useState } from 'react';
import { FluentProvider, webDarkTheme, webLightTheme } from '@fluentui/react-components';
import { AgentConfigView } from './AgentConfigView';
import { BackendsConfigView } from './BackendsConfigView';
import { McpConfigView } from './McpConfigView';
import { TelegramConfigView } from './TelegramConfigView';
import { AppSettingsConfigView } from './AppSettingsConfigView';
import { HeartbeatConfigView } from './HeartbeatConfigView';
import { TokenUsageView } from './TokenUsageView';
import { Sidebar } from './Sidebar';
import { ChatView } from './ChatView';
import { LoginScreen } from './LoginScreen';
import { useChat } from './useChat';
import { checkAuth, fetchAuthStatus, login, setupAuth } from './api';
import clawIcon from './sharpclaw-pincer-detailed.svg';

type Theme = 'light' | 'dark';

function getInitialTheme(): Theme {
  const stored = localStorage.getItem('theme') as Theme | null;
  if (stored === 'dark' || stored === 'light') return stored;
  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

export function App() {
  const [authed, setAuthed] = useState<boolean | null>(null); // null = checking
  const [authConfigured, setAuthConfigured] = useState<boolean>(true);
  const [loginError, setLoginError] = useState<string>();
  const [theme, setTheme] = useState<Theme>(getInitialTheme);
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [isMobileLayout, setIsMobileLayout] = useState(false);
  const [currentView, setCurrentView] = useState<'chat' | 'agents' | 'backends' | 'mcps' | 'telegram' | 'app' | 'heartbeat' | 'token-usage'>('chat');
  const { sessions, active, activeIdx, startSession, setDraftPersona, selectSession, deleteSession, send } = useChat(authed === true);
  const fluentTheme = theme === 'dark' ? webDarkTheme : webLightTheme;

  // Apply theme to <html>
  useEffect(() => {
    if (theme === 'dark') {
      document.documentElement.dataset.theme = 'dark';
    } else {
      delete document.documentElement.dataset.theme;
    }
    localStorage.setItem('theme', theme);
  }, [theme]);

  // On mount, determine whether login is configured and whether current auth cookie is valid.
  useEffect(() => {
    const initAuth = async () => {
      try {
        const status = await fetchAuthStatus();
        setAuthConfigured(status.isConfigured);

        if (!status.isConfigured) {
          setAuthed(false);
          return;
        }

        const ok = await checkAuth();
        setAuthed(ok);
      } catch {
        setAuthed(false);
      }
    };

    void initAuth();
  }, []);

  useEffect(() => {
    const media = window.matchMedia('(max-width: 720px)');
    const apply = (matches: boolean) => {
      setIsMobileLayout(matches);
      if (!matches)
        setSidebarOpen(false);
    };

    apply(media.matches);

    const handleChange = (event: MediaQueryListEvent) => {
      apply(event.matches);
    };

    media.addEventListener('change', handleChange);
    return () => media.removeEventListener('change', handleChange);
  }, []);

  const handleSetup = useCallback(async (username: string, password: string, confirmPassword: string) => {
    setLoginError(undefined);

    try {
      await setupAuth({ username, password, confirmPassword });
      setAuthConfigured(true);
      setAuthed(true);
    } catch (err) {
      setLoginError(err instanceof Error ? err.message : 'Failed to set up login');
    }
  }, []);

  const handleLogin = useCallback(async (username: string, password: string) => {
    setLoginError(undefined);

    try {
      await login({ username, password });
      const ok = await checkAuth();

      if (ok) {
        setAuthed(true);
      } else {
        setLoginError('Login failed');
      }
    } catch (err) {
      setLoginError(err instanceof Error ? err.message : 'Login failed');
    }
  }, []);

  const toggleTheme = useCallback(() => {
    setTheme(t => t === 'dark' ? 'light' : 'dark');
  }, []);

  const handleSelectSession = useCallback((idx: number) => {
    setCurrentView('chat');
    selectSession(idx);
    setSidebarOpen(false);
  }, [selectSession]);

  const handleNewSession = useCallback(() => {
    startSession();
    setCurrentView('chat');
    setSidebarOpen(false);
  }, [startSession]);

  const handleDeleteSession = useCallback(async (sessionId: string) => {
    await deleteSession(sessionId);
    setCurrentView('chat');
  }, [deleteSession]);

  const handleChangeDraftPersona = useCallback((agentId: string, personaName: string) => {
    if (activeIdx < 0) return;
    setDraftPersona(activeIdx, agentId, personaName);
  }, [activeIdx, setDraftPersona]);

  const handleShowAgents = useCallback(() => {
    setCurrentView('agents');
    setSidebarOpen(false);
  }, []);

  const handleShowMcps = useCallback(() => {
    setCurrentView('mcps');
    setSidebarOpen(false);
  }, []);

  const handleShowBackends = useCallback(() => {
    setCurrentView('backends');
    setSidebarOpen(false);
  }, []);

  const handleShowTelegram = useCallback(() => {
    setCurrentView('telegram');
    setSidebarOpen(false);
  }, []);

  const handleShowApp = useCallback(() => {
    setCurrentView('app');
    setSidebarOpen(false);
  }, []);

  const handleShowHeartbeat = useCallback(() => {
    setCurrentView('heartbeat');
    setSidebarOpen(false);
  }, []);

  const handleShowTokenUsage = useCallback(() => {
    setCurrentView('token-usage');
    setSidebarOpen(false);
  }, []);

  const handleShowChats = useCallback(() => {
    setCurrentView('chat');
    setSidebarOpen(false);
  }, []);

  // Loading check
  if (authed === null) return null;

  if (!authed) {
    return (
      <FluentProvider theme={fluentTheme} className="fluent-shell">
        <LoginScreen
          isConfigured={authConfigured}
          onSetup={handleSetup}
          onLogin={handleLogin}
          error={loginError}
        />
      </FluentProvider>
    );
  }

  return (
    <FluentProvider theme={fluentTheme} className="fluent-shell">
      <div className="app">
        {isMobileLayout && sidebarOpen && (
          <div className="sidebar-overlay" onClick={() => setSidebarOpen(false)} />
        )}
        <Sidebar
          sessions={sessions}
          activeIdx={activeIdx}
          onSelect={handleSelectSession}
          onDeleteSession={handleDeleteSession}
          onNewSession={handleNewSession}
          onShowAgents={handleShowAgents}
          onShowBackends={handleShowBackends}
          onShowMcps={handleShowMcps}
          onShowTelegram={handleShowTelegram}
          onShowApp={handleShowApp}
          onShowHeartbeat={handleShowHeartbeat}
          onShowTokenUsage={handleShowTokenUsage}
          theme={theme}
          onToggleTheme={toggleTheme}
          isOpen={sidebarOpen}
          onClose={() => setSidebarOpen(false)}
          currentView={currentView}
        />
        {currentView === 'agents' ? (
          <AgentConfigView onMenuClick={() => setSidebarOpen(true)} />
        ) : currentView === 'backends' ? (
          <BackendsConfigView onMenuClick={() => setSidebarOpen(true)} />
        ) : currentView === 'mcps' ? (
          <McpConfigView onMenuClick={() => setSidebarOpen(true)} />
        ) : currentView === 'telegram' ? (
          <TelegramConfigView onMenuClick={() => setSidebarOpen(true)} />
        ) : currentView === 'app' ? (
          <AppSettingsConfigView onMenuClick={() => setSidebarOpen(true)} />
        ) : currentView === 'heartbeat' ? (
          <HeartbeatConfigView onMenuClick={() => setSidebarOpen(true)} />
        ) : currentView === 'token-usage' ? (
          <TokenUsageView onMenuClick={() => setSidebarOpen(true)} />
        ) : active ? (
          <ChatView
            state={active}
            onSend={send}
            onMenuClick={() => setSidebarOpen(true)}
            onChangePersona={handleChangeDraftPersona}
          />
        ) : (
          <div className="chat-area">
            <div className="chat-header">
              <button className="menu-btn chat-menu-btn" onClick={() => setSidebarOpen(true)} aria-label="Open menu">☰</button>
            </div>
            <div className="empty-state">
              <div className="big" aria-hidden="true">
                <img className="brand-mark-image" src={clawIcon} alt="" />
              </div>
              <div>Select a session or create a new one to get started</div>
            </div>
          </div>
        )}
        {isMobileLayout && !sidebarOpen && (
          <nav className="mobile-tabbar" aria-label="Primary mobile navigation">
            <button
              className={`mobile-tabbar-btn ${currentView === 'chat' ? 'active' : ''}`}
              onClick={handleShowChats}
            >
              Chats
            </button>
            <button
              className={`mobile-tabbar-btn ${currentView === 'agents' ? 'active' : ''}`}
              onClick={handleShowAgents}
            >
              Agents
            </button>
            <button
              className={`mobile-tabbar-btn ${currentView === 'backends' ? 'active' : ''}`}
              onClick={handleShowBackends}
            >
              Backends
            </button>
            <button
              className={`mobile-tabbar-btn ${currentView === 'mcps' ? 'active' : ''}`}
              onClick={handleShowMcps}
            >
              MCPs
            </button>
            <button
              className={`mobile-tabbar-btn ${currentView === 'telegram' ? 'active' : ''}`}
              onClick={handleShowTelegram}
            >
              Telegram
            </button>
          </nav>
        )}
      </div>
    </FluentProvider>
  );
}
