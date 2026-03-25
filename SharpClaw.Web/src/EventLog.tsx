import { useState, useRef, useEffect } from 'react';
import type { StreamItem } from './types';

interface EventLogProps {
  items: StreamItem[];
  /** Whether events are still arriving */
  live: boolean;
}

/** Format an event into a one-line summary */
function eventSummary(item: StreamItem): string {
  const e = item.event;
  switch (e.type) {
    case 'tool_call': {
      const status = item.result
        ? item.result.isError ? '✗' : '✓'
        : '⏳';
      return `${status} 🔧 ${e.tool}`;
    }
    case 'tool_result':
      return `${e.isError ? '✗' : '✓'} Result: ${e.tool}`;
    case 'status':
      return `💭 ${e.message}`;
    case 'usage':
      return `📊 ${e.totalTokens.toLocaleString()} tokens`;
    case 'permission_request':
      return `⚠ Permission: ${e.tool}`;
    case 'token':
      return `📝 Token (${e.text.length} chars)`;
    case 'done':
      return '✅ Complete';
    default:
      return `Event: ${(e as { type: string }).type}`;
  }
}

/** Format the detail view for expanding an event */
function eventDetail(item: StreamItem): string | null {
  const e = item.event;
  switch (e.type) {
    case 'tool_call':
      return [
        `Tool: ${e.tool}`,
        e.input ? `Input: ${JSON.stringify(e.input, null, 2)}` : null,
        item.result ? `\nResult${item.result.isError ? ' (error)' : ''}:\n${item.result.result}` : null,
      ].filter(Boolean).join('\n');
    case 'tool_result':
      return `Tool: ${e.tool}\n${e.isError ? 'Error' : 'Result'}:\n${e.result}`;
    case 'status':
      return null; // summary is enough
    case 'usage':
      return [
        `Provider: ${e.provider}`,
        `Input tokens: ${e.inputTokens.toLocaleString()}`,
        `Output tokens: ${e.outputTokens.toLocaleString()}`,
        `Total tokens: ${e.totalTokens.toLocaleString()}`,
      ].join('\n');
    case 'permission_request':
      return e.input ? JSON.stringify(e.input, null, 2) : null;
    case 'token':
      return e.text;
    default:
      return null;
  }
}

export function EventLog({ items, live }: EventLogProps) {
  const [expanded, setExpanded] = useState(live);
  const [expandedItems, setExpandedItems] = useState<Set<string>>(new Set());
  const scrollRef = useRef<HTMLDivElement>(null);

  // Auto-expand when live, but respect user toggle
  const expandedByUser = useRef<boolean | null>(null);
  useEffect(() => {
    if (expandedByUser.current === null) {
      setExpanded(live);
    }
  }, [live]);

  // Auto-scroll to bottom when new items arrive during live streaming
  useEffect(() => {
    if (live && expanded && scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
    }
  }, [items.length, live, expanded]);

  // Filter out token events from the display — they're noise in the event log
  const displayItems = items.filter(i => i.event.type !== 'token');

  if (displayItems.length === 0) return null;

  const toggleItem = (id: string) => {
    setExpandedItems(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const handleToggle = () => {
    expandedByUser.current = !expanded;
    setExpanded(!expanded);
  };

  const toolCount = displayItems.filter(i => i.event.type === 'tool_call').length;
  const statusCount = displayItems.filter(i => i.event.type === 'status').length;
  const lastStatus = [...displayItems].reverse().find(i => i.event.type === 'status');
  const lastStatusText = lastStatus?.event.type === 'status' ? lastStatus.event.message : null;

  return (
    <div className={`event-log ${live ? 'live' : 'done'} ${expanded ? 'expanded' : 'collapsed'}`}>
      <div className="event-log-header" onClick={handleToggle}>
        <span className={`event-log-chevron ${expanded ? 'open' : ''}`}>▶</span>
        <span className="event-log-title">
          {live ? '⚡ Working' : '📋 Activity'}
        </span>
        <span className="event-log-summary">
          {toolCount > 0 && `${toolCount} tool${toolCount !== 1 ? 's' : ''}`}
          {toolCount > 0 && statusCount > 0 && ' · '}
          {statusCount > 0 && `${statusCount} update${statusCount !== 1 ? 's' : ''}`}
        </span>
        {live && lastStatusText && !expanded && (
          <span className="event-log-latest" title={lastStatusText}>
            {lastStatusText.length > 40 ? lastStatusText.slice(0, 40) + '…' : lastStatusText}
          </span>
        )}
        {live && <span className="event-log-pulse" />}
      </div>

      {expanded && (
        <div className="event-log-body" ref={scrollRef}>
          {displayItems.map(item => {
            const detail = eventDetail(item);
            const isExpanded = expandedItems.has(item.id);
            const hasDetail = detail !== null;

            return (
              <div key={item.id} className="event-log-item">
                <div
                  className={`event-log-item-header ${hasDetail ? 'expandable' : ''}`}
                  onClick={() => hasDetail && toggleItem(item.id)}
                >
                  {hasDetail && (
                    <span className={`event-log-item-chevron ${isExpanded ? 'open' : ''}`}>▶</span>
                  )}
                  <span className="event-log-item-text">{eventSummary(item)}</span>
                </div>
                {isExpanded && detail && (
                  <pre className="event-log-item-detail">{detail}</pre>
                )}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
