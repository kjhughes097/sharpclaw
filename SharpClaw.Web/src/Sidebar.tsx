import { useState, useEffect } from 'react';
import type { Persona } from './types';
import { fetchPersonas } from './api';

interface SidebarProps {
  sessions: { session: { sessionId: string; persona: string }; messages: { role: string }[] }[];
  activeIdx: number;
  onSelect: (idx: number) => void;
  onNewSession: (personaFile: string) => void;
  theme: 'light' | 'dark';
  onToggleTheme: () => void;
  isOpen: boolean;
  onClose: () => void;
}

export function Sidebar({ sessions, activeIdx, onSelect, onNewSession, theme, onToggleTheme, isOpen, onClose }: SidebarProps) {
  const [showModal, setShowModal] = useState(false);
  const [personas, setPersonas] = useState<Persona[]>([]);

  useEffect(() => {
    fetchPersonas().then(setPersonas).catch(console.error);
  }, []);

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
      <button className="new-session-btn" onClick={() => setShowModal(true)}>
        + New Chat
      </button>
      <div className="session-list">
        {sessions.map((s, i) => (
          <div
            key={s.session.sessionId}
            className={`session-item ${i === activeIdx ? 'active' : ''}`}
            onClick={() => onSelect(i)}
          >
            <span>{s.session.persona}</span>
            <span className="session-persona">
              {s.messages.length} message{s.messages.length !== 1 ? 's' : ''}
            </span>
          </div>
        ))}
      </div>

      {showModal && (
        <div className="modal-overlay" onClick={() => setShowModal(false)}>
          <div className="modal" onClick={e => e.stopPropagation()}>
            <h2>Choose a Persona</h2>
            {personas.map(p => (
              <div
                key={p.file}
                className="persona-option"
                onClick={() => {
                  setShowModal(false);
                  onNewSession(p.file);
                }}
              >
                <div className="name">{p.name}</div>
                <div className="meta">
                  {p.backend} · {p.mcpServers.length} tool server{p.mcpServers.length !== 1 ? 's' : ''}
                </div>
              </div>
            ))}
            {personas.length === 0 && (
              <div style={{ color: 'var(--text-dim)', padding: '12px 0' }}>
                No personas found. Add .agent.md files to the personas directory.
              </div>
            )}
            <button className="modal-cancel" onClick={() => setShowModal(false)}>
              Cancel
            </button>
          </div>
        </div>
      )}
    </aside>
  );
}
