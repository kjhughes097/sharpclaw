import { useCallback, useEffect, useState } from 'react';
import { FluentProvider, webDarkTheme, webLightTheme } from '@fluentui/react-components';
import { AgentConfigView } from './AgentConfigView';
import { McpConfigView } from './McpConfigView';
import { Sidebar } from './Sidebar';
import { ChatView } from './ChatView';
import { LoginScreen } from './LoginScreen';
import { useChat } from './useChat';
import { checkAuth, clearApiKey, hasApiKey, setApiKey } from './api';
import clawIcon from './sharpclaw-pincer-detailed.svg';

type Theme = 'light' | 'dark';

function getInitialTheme(): Theme {
  const stored = localStorage.getItem('theme') as Theme | null;
  if (stored === 'dark' || stored === 'light') return stored;
  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

export function App() {
  const [authed, setAuthed] = useState<boolean | null>(null); // null = checking
  const [loginError, setLoginError] = useState<string>();
  const [theme, setTheme] = useState<Theme>(getInitialTheme);
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [isMobileLayout, setIsMobileLayout] = useState(false);
  const [currentView, setCurrentView] = useState<'chat' | 'agents' | 'mcps'>('chat');
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

  // On mount, verify any stored key
  useEffect(() => {
    if (!hasApiKey()) { setAuthed(false); return; }
    checkAuth().then(ok => {
      if (!ok) clearApiKey();
      setAuthed(ok);
    });
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

  const handleLogin = useCallback(async (key: string) => {
    setLoginError(undefined);
    setApiKey(key);
    const ok = await checkAuth();
    if (ok) {
      setAuthed(true);
    } else {
      clearApiKey();
      setLoginError('Invalid API key');
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

  const handleShowChats = useCallback(() => {
    setCurrentView('chat');
    setSidebarOpen(false);
  }, []);

  // Loading check
  if (authed === null) return null;

  if (!authed) {
    return (
      <FluentProvider theme={fluentTheme} className="fluent-shell">
        <LoginScreen onLogin={handleLogin} error={loginError} />
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
          onShowMcps={handleShowMcps}
          theme={theme}
          onToggleTheme={toggleTheme}
          isOpen={sidebarOpen}
          onClose={() => setSidebarOpen(false)}
          currentView={currentView}
        />
        {currentView === 'agents' ? (
          <AgentConfigView onMenuClick={() => setSidebarOpen(true)} />
        ) : currentView === 'mcps' ? (
          <McpConfigView onMenuClick={() => setSidebarOpen(true)} />
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
              className={`mobile-tabbar-btn ${currentView === 'mcps' ? 'active' : ''}`}
              onClick={handleShowMcps}
            >
              MCPs
            </button>
          </nav>
        )}
      </div>
    </FluentProvider>
  );
}
