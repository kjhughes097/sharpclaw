import { lazy, Suspense, useEffect, useState } from 'react';
import { useParams, useSearchParams } from 'react-router';
import { LoadingState } from '../../components/ui';
import { ThreadList } from './ThreadList';
import { ThreadView } from './ThreadView';

// The trace panel is loaded on first open to keep the initial bundle small.
const TracePanel = lazy(() => import('../audit/TracePanel').then((m) => ({ default: m.TracePanel })));

/**
 * `/chats` and `/chats/:threadId`: the thread list beside the thread view on desktop, separate
 * screens on mobile; `?panel=trace` opens the trace (side panel or full-screen sheet).
 */
export function ChatsPage() {
  const { threadId } = useParams();
  const [params, setParams] = useSearchParams();
  const traceOpen = !!threadId && params.get('panel') === 'trace';
  const [highlight, setHighlight] = useState<string | null>(null);

  const closeTrace = () => {
    const next = new URLSearchParams(params);
    next.delete('panel');
    setParams(next, { replace: true });
  };

  useEffect(() => {
    if (!traceOpen) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') closeTrace();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [traceOpen]);

  useEffect(() => setHighlight(null), [threadId]);

  return (
    <div className="flex min-h-0 flex-1">
      <div
        className={`min-h-0 w-full flex-col border-r border-border bg-surface md:flex md:w-80 md:shrink-0 ${threadId ? 'hidden' : 'flex'}`}
      >
        <ThreadList activeId={threadId} />
      </div>
      {threadId ? (
        <div className="flex min-h-0 min-w-0 flex-1">
          <ThreadView
            key={threadId}
            threadId={threadId}
            backLink
            highlightMessageId={highlight}
            onOpenTrace={() => {
              const next = new URLSearchParams(params);
              next.set('panel', 'trace');
              setParams(next);
            }}
          />
          {traceOpen && (
            <aside
              aria-label="Trace"
              className="fixed inset-0 z-30 overflow-y-auto bg-surface lg:static lg:z-auto lg:w-96 lg:shrink-0 lg:border-l lg:border-border"
            >
              <Suspense fallback={<LoadingState label="Loading trace" />}>
                <TracePanel
                  threadId={threadId}
                  onClose={closeTrace}
                  onSelectTurn={(id) => {
                    setHighlight(id);
                    // On small screens the sheet covers the chat: close it to show the message.
                    if (!window.matchMedia('(min-width: 1024px)').matches) closeTrace();
                  }}
                />
              </Suspense>
            </aside>
          )}
        </div>
      ) : (
        <div className="hidden flex-1 items-center justify-center text-sm text-muted md:flex">
          Choose a chat or start a new one.
        </div>
      )}
    </div>
  );
}
