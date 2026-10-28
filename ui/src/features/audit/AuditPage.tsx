import { Tabs } from 'radix-ui';
import { type ReactNode } from 'react';
import { useSearchParams } from 'react-router';
import { useAgents, useSystem } from '../../api/hooks';
import { LLM_STATUSES, TIME_WINDOWS, TOOL_OUTCOMES, type AuditTab } from './auditFormat';
import { type AgentNames, type AuditFilters } from './auditParts';
import { Filters } from './AuditFilterBar';
import { LlmCallList } from './LlmCallList';
import { ToolCallList } from './ToolCallList';

/**
 * `/audit` (3.3 spec section 9.8): LLM and tool calls, newest first, metadata only. Tab and filters
 * live in the URL so a view can be deep-linked and survives a reload.
 */
export function AuditPage() {
  const [params, setParams] = useSearchParams();
  // Loads the server clock offset: the time windows and the "unreported" marker use server time.
  useSystem();
  const agents = useAgents();

  const tab: AuditTab = params.get('tab') === 'tools' ? 'tools' : 'llm';
  const statuses: readonly string[] = tab === 'llm' ? LLM_STATUSES : TOOL_OUTCOMES;
  const rawStatus = params.get('status') ?? undefined;
  const rawWindow = params.get('window') ?? undefined;
  const filters: AuditFilters = {
    agent: params.get('agent') || undefined,
    // Values that don't belong to this tab are ignored rather than sent.
    status: rawStatus && statuses.includes(rawStatus) ? rawStatus : undefined,
    window: TIME_WINDOWS.some((w) => w.value === rawWindow) ? rawWindow : undefined,
    threadId: params.get('threadId') || undefined,
  };

  const agentNames: AgentNames = new Map((agents.data ?? []).map((a) => [a.id, a.name]));
  const agentOptions = (agents.data ?? []).map((a) => a.name);
  if (filters.agent && !agentOptions.includes(filters.agent)) agentOptions.push(filters.agent);

  const update = (changes: Record<string, string | undefined>) =>
    setParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        for (const [key, value] of Object.entries(changes)) {
          if (value) next.set(key, value);
          else next.delete(key);
        }
        return next;
      },
      { replace: true },
    );

  return (
    <div className="relative flex min-h-0 flex-1 flex-col overflow-y-auto">
      <div className="mx-auto w-full max-w-7xl p-4">
        <h1 className="text-xl font-semibold">Audit</h1>
        <p className="mt-1 text-sm text-muted">
          Metadata for every LLM and tool call. Prompts, replies and tool arguments are not shown.
        </p>

        <Tabs.Root
          value={tab}
          // Status values differ per tab, so switching tabs drops the status filter.
          onValueChange={(value) => update({ tab: value === 'tools' ? 'tools' : 'llm', status: undefined })}
          className="mt-4"
        >
          <Tabs.List aria-label="Audit tables" className="flex gap-1 border-b border-border">
            <TabTrigger value="llm">LLM calls</TabTrigger>
            <TabTrigger value="tools">Tool calls</TabTrigger>
          </Tabs.List>

          <Filters statuses={statuses} filters={filters} agentOptions={agentOptions} onChange={update} />

          <Tabs.Content value="llm" className="mt-4 outline-none">
            <LlmCallList filters={filters} agentNames={agentNames} />
          </Tabs.Content>
          <Tabs.Content value="tools" className="mt-4 outline-none">
            <ToolCallList filters={filters} agentNames={agentNames} />
          </Tabs.Content>
        </Tabs.Root>
      </div>
    </div>
  );
}

function TabTrigger({ value, children }: { value: AuditTab; children: ReactNode }) {
  return (
    <Tabs.Trigger
      value={value}
      className="-mb-px min-h-11 border-b-2 border-transparent px-4 text-sm font-medium text-muted data-[state=active]:border-accent data-[state=active]:text-fg"
    >
      {children}
    </Tabs.Trigger>
  );
}
