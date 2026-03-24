import { useCallback, useEffect, useState } from 'react';
import { Sidebar } from './Sidebar';
import { ChatView } from './ChatView';
import { LoginScreen } from './LoginScreen';
import { useChat } from './useChat';
import { checkAuth, clearApiKey, hasApiKey, setApiKey } from './api';

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
  const { sessions, active, activeIdx, startSession, selectSession, send } = useChat();

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
    selectSession(idx);
    setSidebarOpen(false);
  }, [selectSession]);

  // Loading check
  if (authed === null) return null;

  if (!authed) return <LoginScreen onLogin={handleLogin} error={loginError} />;

  return (
    <div className="app">
      {sidebarOpen && (
        <div className="sidebar-overlay" onClick={() => setSidebarOpen(false)} />
      )}
      <Sidebar
        sessions={sessions}
        activeIdx={activeIdx}
        onSelect={handleSelectSession}
        onNewSession={startSession}
        theme={theme}
        onToggleTheme={toggleTheme}
        isOpen={sidebarOpen}
        onClose={() => setSidebarOpen(false)}
      />
      {active ? (
        <ChatView state={active} onSend={send} onMenuClick={() => setSidebarOpen(true)} />
      ) : (
        <div className="chat-area">
          <div className="chat-header">
            <button className="menu-btn" onClick={() => setSidebarOpen(true)} aria-label="Open menu">☰</button>
          </div>
          <div className="empty-state">
            <div className="big">🐾</div>
            <div>Select a session or create a new one to get started</div>
          </div>
        </div>
      )}
    </div>
  );
}
