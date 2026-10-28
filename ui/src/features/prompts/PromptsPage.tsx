import { Dialog as RadixDialog } from 'radix-ui';
import { lazy, Suspense, useEffect, useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router';
import { useAgents, useSystem } from '../../api/hooks';
import type { AgentDto } from '../../api/types';
import { useToast } from '../../app/toast';
import { LoadingState } from '../../components/ui';
import { getLastAgent, setLastAgent } from '../../lib/prefs';
import { useThreadDetail, useThreadTrace } from '../chat/threadQueries';
import { ThreadView } from '../chat/ThreadView';
import { useUpdateThread } from '../chat/useUpdateThread';
import { SessionList } from './SessionList';
import { StatusBar } from './StatusBar';

// The trace panel is loaded on first open to keep the initial bundle small.
const TracePanel = lazy(() => import('../audit/TracePanel').then((m) => ({ default: m.TracePanel })));

const SELECT = 'min-h-8 rounded border border-border bg-surface-2 px-1.5 text-sm';

/**
 * Which agent the toolbar shows: `?agent`, else the open thread's agent, else the last one picked,
 * else the first enabled agent (3.3.1 spec section 6.1).
 */
export function pickAgent(
  agents: readonly AgentDto[],
  requested: string | null,
  threadAgent: AgentDto | undefined,
  last: string | null,
): AgentDto | undefined {
  const enabled = [...agents].filter((a) => a.enabled).sort((a, b) => a.name.localeCompare(b.name));
  return (
    (requested ? agents.find((a) => a.name === requested) : undefined) ??
    threadAgent ??
    (last ? enabled.find((a) => a.name === last) : undefined) ??
    enabled[0]
  );
}

function ModelSelect({
  agent,
  models,
  threadId,
  threadModel,
  newModel,
  onNewModel,
}: {
  agent: AgentDto;
  models: string[];
  threadId: string | undefined;
  threadModel: string | null;
  newModel: string;
  onNewModel: (model: string) => void;
}) {
  const toast = useToast();
  const update = useUpdateThread(threadId ?? '');
  const value = threadId ? (threadModel ?? '') : newModel;
  const options = models.filter((m) => m !== agent.model);
  // An override that has since left the allow-list still shows as selected.
  if (value && !options.includes(value)) options.push(value);

  return (
    <label className="inline-flex items-center gap-1.5">
      Model
      <select
        className={SELECT}
        value={value}
        disabled={update.isPending}
        onChange={(e) => {
          const model = e.target.value;
          if (!threadId) return onNewModel(model);
          update.mutate(
            { model },
            { onSuccess: () => toast('Model changes apply from the next reply.', 'success') },
          );
        }}
      >
        <option value="">{agent.model} (default)</option>
        {options.map((m) => (
          <option key={m} value={m}>
            {m}
          </option>
        ))}
      </select>
    </label>
  );
}

/**
 * `/prompts` and `/prompts/:threadId` (3.3.1 spec section 6): agent and model pickers, the
 * sessions sidebar (a drawer on phones), the thread, and the status bar. `?panel=trace` opens the
 * trace beside the thread.
 */
export function PromptsPage() {
  const { threadId } = useParams();
  const [params, setParams] = useSearchParams();
  const navigate = useNavigate();
  const agents = useAgents();
  const system = useSystem();
  const detail = useThreadDetail(threadId);
  const trace = useThreadTrace(threadId);
  const [newModel, setNewModel] = useState('');
  const [collapsed, setCollapsed] = useState(false);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [highlight, setHighlight] = useState<string | null>(null);

  const all = agents.data ?? [];
  const agent = pickAgent(all, params.get('agent'), detail.data?.agent, getLastAgent());
  const enabled = all.filter((a) => a.enabled).sort((a, b) => a.name.localeCompare(b.name));
  const choices = agent && !agent.enabled ? [...enabled, agent] : enabled;
  const models = system.data?.models ?? [];
  const traceOpen = !!threadId && params.get('panel') === 'trace';

  const chooseAgent = (name: string) => {
    setLastAgent(name);
    setNewModel('');
    navigate(`/prompts?agent=${encodeURIComponent(name)}`);
  };

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

  const sessions = (extra: { onCollapse?: () => void; onNavigate?: () => void }) => (
    <SessionList agent={agent} model={newModel} activeId={threadId} {...extra} />
  );

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <h1 className="sr-only">Prompts</h1>
      <div className="flex flex-wrap items-center gap-x-5 gap-y-1 border-b border-border px-3 py-1.5 text-sm">
        <button
          type="button"
          onClick={() => setDrawerOpen(true)}
          className="min-h-8 rounded border border-border px-2 md:hidden"
        >
          Sessions
        </button>
        {collapsed && (
          <button
            type="button"
            onClick={() => setCollapsed(false)}
            className="hidden min-h-8 rounded border border-border px-2 md:inline-block"
          >
            Show sessions
          </button>
        )}
        <label className="inline-flex items-center gap-1.5">
          Agent
          <select
            className={SELECT}
            value={agent?.name ?? ''}
            onChange={(e) => chooseAgent(e.target.value)}
            disabled={choices.length === 0}
          >
            {choices.length === 0 && <option value="">No enabled agents</option>}
            {choices.map((a) => (
              <option key={a.id} value={a.name}>
                {a.name}
                {a.enabled ? '' : ' (disabled)'}
              </option>
            ))}
          </select>
        </label>
        {agent && models.length > 0 && (
          <ModelSelect
            agent={agent}
            models={models}
            threadId={threadId}
            threadModel={detail.data?.thread.model ?? null}
            newModel={newModel}
            onNewModel={setNewModel}
          />
        )}
      </div>

      <div className="flex min-h-0 flex-1">
        {!collapsed && (
          <aside
            aria-label="Sessions"
            className="hidden w-64 shrink-0 flex-col border-r border-border bg-surface md:flex lg:w-72 xl:w-80"
          >
            {sessions({ onCollapse: () => setCollapsed(true) })}
          </aside>
        )}
        <RadixDialog.Root open={drawerOpen} onOpenChange={setDrawerOpen}>
          <RadixDialog.Portal>
            <RadixDialog.Overlay className="fixed inset-0 z-40 bg-black/50" />
            <RadixDialog.Content className="fixed inset-y-0 left-0 z-50 flex w-72 flex-col bg-surface shadow-xl">
              <RadixDialog.Title className="sr-only">Sessions</RadixDialog.Title>
              <RadixDialog.Description className="sr-only">
                Pick a session or start a new one.
              </RadixDialog.Description>
              {sessions({ onNavigate: () => setDrawerOpen(false) })}
            </RadixDialog.Content>
          </RadixDialog.Portal>
        </RadixDialog.Root>

        {threadId ? (
          <div className="flex min-h-0 min-w-0 flex-1">
            <ThreadView
              key={threadId}
              threadId={threadId}
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
          <div className="flex flex-1 items-center justify-center p-6 text-sm text-muted">
            Pick a session or start a new one.
          </div>
        )}
      </div>

      <StatusBar thread={detail.data?.thread} agent={detail.data?.agent ?? agent} trace={trace.data} />
    </div>
  );
}
