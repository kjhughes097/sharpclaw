interface SidebarProps {
  sessions: { session: { sessionId: string; persona: string }; messages: { role: string; content?: string }[]; createdAt: string; lastActivityAt: string }[];
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

function plainTextPreview(text: string | undefined, maxLength: number): string {
  const normalized = (text ?? '')
    .replace(/```[\s\S]*?```/g, ' code block ')
    .replace(/`([^`]+)`/g, '$1')
    .replace(/[#>*_~-]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();

  if (!normalized) return '';
  return normalized.length > maxLength ? `${normalized.slice(0, maxLength - 1).trimEnd()}…` : normalized;
}

function sessionTitle(messages: { role: string; content?: string }[], fallback: string): string {
  const firstUser = messages.find(message => message.role === 'user');
  const title = plainTextPreview(firstUser?.content, 42);
  return title || fallback;
}

function sessionMeta(messages: { role: string; content?: string }[], persona: string): string {
  const lastMessage = messages[messages.length - 1];
  const preview = plainTextPreview(lastMessage?.content, 56);
  if (preview) return preview;

  const count = messages.length;
  return `${persona} · ${count} message${count !== 1 ? 's' : ''}`;
}

function formatSessionTime(value: string): string | null {
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime()))
    return null;

  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(parsed);
}

function sessionTooltip(createdAt: string, lastActivityAt: string): string {
  const created = formatSessionTime(createdAt);
  const lastActive = formatSessionTime(lastActivityAt);

  if (lastActive && created && lastActive !== created)
    return `Last active ${lastActive}\nCreated ${created}`;

  if (lastActive)
    return `Last active ${lastActive}`;

  if (created)
    return `Created ${created}`;

  return 'Session time unavailable';
}

function sessionAge(lastActivityAt: string): string {
  const parsed = new Date(lastActivityAt);
  if (Number.isNaN(parsed.getTime()))
    return '';

  const elapsedMs = Date.now() - parsed.getTime();
  if (elapsedMs < 60_000)
    return 'just now';

  const minutes = Math.floor(elapsedMs / 60_000);
  if (minutes < 60)
    return `${minutes}m ago`;

  const hours = Math.floor(minutes / 60);
  if (hours < 24)
    return `${hours}h ago`;

  const days = Math.floor(hours / 24);
  if (days < 7)
    return `${days}d ago`;

  const weeks = Math.floor(days / 7);
  if (weeks < 5)
    return `${weeks}w ago`;

  const months = Math.floor(days / 30);
  if (months < 12)
    return `${months}mo ago`;

  const years = Math.floor(days / 365);
  return `${years}y ago`;
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
            title={sessionTooltip(s.createdAt, s.lastActivityAt)}
          >
            <span className="session-title">{sessionTitle(s.messages, s.session.persona)}</span>
            <span className="session-persona">
              {sessionMeta(s.messages, s.session.persona)}
            </span>
            <span className="session-age">{sessionAge(s.lastActivityAt)}</span>
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
