/** Query keys in one place, so event handlers invalidate exactly what pages read. */
export const keys = {
  session: ['session'] as const,
  system: ['system'] as const,
  agents: ['agents'] as const,
  agent: (name: string) => ['agents', name] as const,
  tokens: (name: string) => ['tokens', name] as const,
  threads: ['threads'] as const,
  threadList: (filters: { agentId?: string | undefined; archived: boolean }) => ['threads', filters] as const,
  thread: (id: string) => ['thread', id] as const,
  messages: (id: string) => ['messages', id] as const,
  trace: (id: string) => ['trace', id] as const,
  approvals: ['approvals'] as const,
  approvalList: (status: string) => ['approvals', status] as const,
  audit: ['audit'] as const,
  auditList: (tab: 'llm' | 'tools', filters: Record<string, string | undefined>) => ['audit', tab, filters] as const,
};
