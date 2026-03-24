import { useCallback, useEffect, useState } from 'react';
import { Sidebar } from './Sidebar';
import { ChatView } from './ChatView';
import { LoginScreen } from './LoginScreen';
import { useChat } from './useChat';
import { checkAuth, clearApiKey, hasApiKey, setApiKey } from './api';

export function App() {
  const [authed, setAuthed] = useState<boolean | null>(null); // null = checking
  const [loginError, setLoginError] = useState<string>();
  const { sessions, active, activeIdx, startSession, selectSession, send } = useChat();

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

  // Loading check
  if (authed === null) return null;

  if (!authed) return <LoginScreen onLogin={handleLogin} error={loginError} />;

  return (
    <div className="app">
      <Sidebar
        sessions={sessions}
        activeIdx={activeIdx}
        onSelect={selectSession}
        onNewSession={startSession}
      />
      {active ? (
        <ChatView state={active} onSend={send} />
      ) : (
        <div className="chat-area">
          <div className="empty-state">
            <div className="big">🐾</div>
            <div>Select a session or create a new one to get started</div>
          </div>
        </div>
      )}
    </div>
  );
}
