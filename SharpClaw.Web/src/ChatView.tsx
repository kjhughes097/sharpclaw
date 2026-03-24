import { useState, useRef, useEffect } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import type { SessionState } from './useChat';
import { PermissionCard } from './PermissionCard';
import { EventLog } from './EventLog';

interface ChatViewProps {
  state: SessionState;
  onSend: (text: string) => void;
  onMenuClick: () => void;
}

/** Derive a two-letter avatar from the persona name */
function avatarInitials(name: string): string {
  const words = name.split(/\s+/);
  if (words.length >= 2) return (words[0][0] + words[1][0]).toUpperCase();
  return name.slice(0, 2).toUpperCase();
}

export function ChatView({ state, onSend, onMenuClick }: ChatViewProps) {
  const [input, setInput] = useState('');
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  // Auto-scroll on new content
  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [state.messages.length, state.streamItems.length]);

  // Auto-resize textarea
  useEffect(() => {
    const ta = textareaRef.current;
    if (ta) {
      ta.style.height = 'auto';
      ta.style.height = Math.min(ta.scrollHeight, 200) + 'px';
    }
  }, [input]);

  const handleSend = () => {
    const text = input.trim();
    if (!text || state.streaming) return;
    setInput('');
    onSend(text);
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handleSend();
    }
  };

  // Build streaming text from token events
  const streamingText = state.streamItems
    .filter(i => i.event.type === 'token')
    .map(i => (i.event as { text: string }).text)
    .join('');

  const initials = avatarInitials(state.session.persona);

  return (
    <div className="chat-area">
      <div className="chat-header">
        <button className="menu-btn" onClick={onMenuClick} aria-label="Open menu">☰</button>
        <div className="avatar">{initials}</div>
        <span>{state.session.persona}</span>
      </div>

      <div className="chat-messages">
        {state.messages.length === 0 && !state.streaming && (
          <div className="empty-state">
            <div className="big">🐾</div>
            <div>Start a conversation with {state.session.persona}</div>
          </div>
        )}

        {state.messages.map((msg, i) => {
          // Count assistant messages up to this point to index into eventLogs
          const assistantIdx = msg.role === 'assistant'
            ? state.messages.slice(0, i + 1).filter(m => m.role === 'assistant').length - 1
            : -1;
          const eventLog = assistantIdx >= 0 ? state.eventLogs[assistantIdx] : undefined;

          return (
            <div key={i} className={`message ${msg.role}`}>
              <div className="avatar">
                {msg.role === 'user' ? '👤' : initials}
              </div>
              <div className="bubble">
                {msg.role === 'assistant' && eventLog && eventLog.length > 0 && (
                  <EventLog items={eventLog} live={false} />
                )}
                {msg.role === 'assistant' ? (
                  <ReactMarkdown remarkPlugins={[remarkGfm]}>{msg.content}</ReactMarkdown>
                ) : (
                  msg.content
                )}
              </div>
            </div>
          );
        })}

        {/* Streaming assistant turn */}
        {state.streaming && (
          <div className="message assistant">
            <div className="avatar">{initials}</div>
            <div className="bubble">
              {/* Event log card — shows tool calls, status updates, etc. */}
              <EventLog items={state.streamItems} live={true} />

              {/* Permission requests rendered separately (needs interaction) */}
              {state.streamItems
                .filter(i => i.event.type === 'permission_request')
                .map(item => (
                  <PermissionCard
                    key={item.id}
                    sessionId={state.session.sessionId}
                    event={item.event as { tool: string; input: Record<string, unknown> | null; requestId: string; type: 'permission_request' }}
                  />
                ))}

              {/* Streaming markdown text */}
              {streamingText && (
                <div className="streaming-cursor">
                  <ReactMarkdown remarkPlugins={[remarkGfm]}>{streamingText}</ReactMarkdown>
                </div>
              )}

              {!streamingText && state.streamItems.length === 0 && (
                <span className="streaming-cursor" />
              )}
            </div>
          </div>
        )}

        <div ref={messagesEndRef} />
      </div>

      <div className="input-bar">
        <textarea
          ref={textareaRef}
          placeholder="Type a message…"
          value={input}
          onChange={e => setInput(e.target.value)}
          onKeyDown={handleKeyDown}
          rows={1}
          disabled={state.streaming}
        />
        <button
          className="send-btn"
          onClick={handleSend}
          disabled={state.streaming || !input.trim()}
        >
          Send
        </button>
      </div>
    </div>
  );
}
