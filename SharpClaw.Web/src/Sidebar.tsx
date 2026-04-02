import type { MouseEvent } from 'react';
import { Delete16Regular, Dismiss20Regular } from '@fluentui/react-icons';
import type { StreamItem } from './types';
import clawIcon from './sharpclaw-pincer-detailed.svg';

interface SidebarProps {
  sessions: {
    session: { sessionId: string; persona: string };
    messages: { role: string; content?: string }[];
    createdAt: string;
    lastActivityAt: string;
    eventLogs?: StreamItem[][];
    streamItems?: StreamItem[];
    streaming: boolean;
  }[];
  activeIdx: number;
  onSelect: (idx: number) => void;
  onDeleteSession: (sessionId: string) => Promise<void>;
  onNewSession: () => void;
  onShowAgents: () => void;
  onShowMcps: () => void;
  onShowTelegram: () => void;
  theme: 'light' | 'dark';
  onToggleTheme: () => void;
  isOpen: boolean;
  onClose: () => void;
  currentView: 'chat' | 'agents' | 'mcps' | 'telegram';
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

function messageCountLabel(messages: { role: string; content?: string }[]): string {
  const count = messages.length;
  return `${count} msg${count !== 1 ? 's' : ''}`;
}

function estimateTokenCount(messages: { role: string; content?: string }[]): number {
  const text = messages
    .map(message => message.content ?? '')
    .join(' ')
    .trim();

  if (!text)
    return 0;

  // Rough GPT/Claude-style estimate: ~4 characters per token for English prose.
  return Math.max(1, Math.ceil(text.length / 4));
}

function exactTokenCount(eventLogs: StreamItem[][] = [], streamItems: StreamItem[] = []): number {
  const usageItems = [...eventLogs.flat(), ...streamItems]
    .map(item => item.event)
    .filter((event): event is Extract<StreamItem['event'], { type: 'usage' }> => event.type === 'usage');

  return usageItems.reduce((total, event) => total + event.totalTokens, 0);
}

function tokenCountLabel(
  messages: { role: string; content?: string }[],
  eventLogs: StreamItem[][] = [],
  streamItems: StreamItem[] = [],
): string {
  const exact = exactTokenCount(eventLogs, streamItems);
  const count = exact > 0 ? exact : estimateTokenCount(messages);
  const suffix = exact > 0 ? 'tok' : '~tok';

  if (count >= 1_000)
    return `${(count / 1_000).toFixed(count >= 10_000 ? 0 : 1)}k ${suffix}`;

  return `${count} ${suffix}`;
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

export function Sidebar({ sessions, activeIdx, onSelect, onDeleteSession, onNewSession, onShowAgents, onShowMcps, onShowTelegram, theme, onToggleTheme, isOpen, onClose, currentView }: SidebarProps) {
  const handleDeleteClick = async (
    event: MouseEvent<HTMLButtonElement>,
    sessionId: string,
    title: string,
    streaming: boolean,
  ) => {
    event.stopPropagation();

    if (streaming) {
      window.alert('This chat is still streaming. Wait for it to finish before deleting it.');
      return;
    }

    const confirmed = window.confirm(`Delete this chat?\n\n${title}\n\nThis removes it from the UI and the database.`);
    if (!confirmed)
      return;

    try {
      await onDeleteSession(sessionId);
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      window.alert(`Failed to delete chat: ${message}`);
    }
  };

  return (
    <aside className={`sidebar${isOpen ? ' open' : ''}`}>
      <div className="sidebar-header">
        <span className="logo" aria-hidden="true">
          <img className="brand-mark-image" src={clawIcon} alt="" />
        </span>
        <span className="sidebar-brand-name">SharpClaw</span>
        <div className="sidebar-header-actions">
          <button
            type="button"
            className="sidebar-drawer-close"
            onClick={onClose}
            aria-label="Close menu"
          >
            <Dismiss20Regular />
          </button>
          <button
            className="theme-toggle-btn"
            data-theme={theme}
            onClick={onToggleTheme}
            aria-label={`Switch to ${theme === 'dark' ? 'light' : 'dark'} mode`}
            aria-pressed={theme === 'dark'}
            title={`Switch to ${theme === 'dark' ? 'light' : 'dark'} mode`}
          >
            <span className="theme-toggle-thumb" aria-hidden="true" />
            <span className="theme-toggle-option theme-toggle-option-light" aria-hidden="true">
              <span className="theme-toggle-option-icon">☀</span>
            </span>
            <span className="theme-toggle-option theme-toggle-option-dark" aria-hidden="true">
              <span className="theme-toggle-option-icon">☾</span>
            </span>
          </button>
        </div>
      </div>
      <button className="new-session-btn" onClick={onNewSession}>
        + New Chat
      </button>
      <div className="sidebar-section-label">Chats</div>
      <div className="session-list">
        {sessions.map((s, i) => {
          const title = sessionTitle(s.messages, s.session.persona);

          return (
            <div
              key={s.session.sessionId}
              className={`session-item ${currentView === 'chat' && i === activeIdx ? 'active' : ''}`}
              onClick={() => onSelect(i)}
              title={sessionTooltip(s.createdAt, s.lastActivityAt)}
            >
              <div className="session-row">
                <span className="session-title">{title}</span>
                <button
                  type="button"
                  className="session-delete-btn"
                  aria-label={`Delete chat ${title}`}
                  title={s.streaming ? 'Cannot delete a chat while it is streaming' : 'Delete chat'}
                  onClick={event => void handleDeleteClick(event, s.session.sessionId, title, s.streaming)}
                >
                  <Delete16Regular />
                </button>
              </div>
              <span className="session-persona">
                {sessionMeta(s.messages, s.session.persona)}
              </span>
              <div className="session-pills">
                <span className="session-pill session-pill-time">{sessionAge(s.lastActivityAt)}</span>
                <span className="session-pill">{messageCountLabel(s.messages)}</span>
                <span className="session-pill">{tokenCountLabel(s.messages, s.eventLogs, s.streamItems)}</span>
              </div>
            </div>
          );
        })}
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
        <button
          className={`sidebar-nav-item ${currentView === 'telegram' ? 'active' : ''}`}
          onClick={onShowTelegram}
        >
          Telegram
        </button>
      </div>
    </aside>
  );
}
