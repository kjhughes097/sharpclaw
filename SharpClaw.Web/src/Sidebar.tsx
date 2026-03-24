interface SidebarProps {
  sessions: { session: { sessionId: string; persona: string }; messages: { role: string }[] }[];
  activeIdx: number;
  onSelect: (idx: number) => void;
  onNewSession: () => void;
  onShowAgents: () => void;
  onShowMcps: () => void;
  theme: 'light' | 'dark';
  onToggleTheme: () => void;
  isOpen: boolean;
  onClose: () => void;
  currentView: 'chat' | 'agents' | 'mcps';
}

export function Sidebar({ sessions, activeIdx, onSelect, onNewSession, onShowAgents, onShowMcps, theme, onToggleTheme, isOpen, onClose, currentView }: SidebarProps) {
  return (
    <aside className={`sidebar${isOpen ? ' open' : ''}`}>
      <div className="sidebar-header">
        <span className="logo">🐾</span>
        <span>SharpClaw</span>
        <div className="sidebar-header-actions">
          <button
            className="theme-toggle-btn"
            onClick={onToggleTheme}
            aria-label={`Switch to ${theme === 'dark' ? 'light' : 'dark'} mode`}
            title={`Switch to ${theme === 'dark' ? 'light' : 'dark'} mode`}
          >
            {theme === 'dark' ? '☀️' : '🌙'}
          </button>
          <button className="sidebar-close-btn" onClick={onClose} aria-label="Close sidebar">✕</button>
        </div>
      </div>
      <button className="new-session-btn" onClick={onNewSession}>
        + New Chat
      </button>
      <div className="sidebar-section-label">Chats</div>
      <div className="session-list">
        {sessions.map((s, i) => (
          <div
            key={s.session.sessionId}
            className={`session-item ${currentView === 'chat' && i === activeIdx ? 'active' : ''}`}
            onClick={() => onSelect(i)}
          >
            <span>{s.session.persona}</span>
            <span className="session-persona">
              {s.messages.length} message{s.messages.length !== 1 ? 's' : ''}
            </span>
          </div>
        ))}
      </div>

      <div className="sidebar-section-label">Configure</div>
      <div className="sidebar-nav-list">
        <button
          className={`sidebar-nav-item ${currentView === 'agents' ? 'active' : ''}`}
          onClick={onShowAgents}
        >
          Agents
        </button>
        <button
          className={`sidebar-nav-item ${currentView === 'mcps' ? 'active' : ''}`}
          onClick={onShowMcps}
        >
          MCPs
        </button>
      </div>
    </aside>
  );
}
