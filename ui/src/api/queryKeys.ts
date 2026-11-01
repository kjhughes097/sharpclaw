/** Query keys in one place, so event handlers invalidate exactly what pages read. */
export const keys = {
  session: ['session'] as const,
  system: ['system'] as const,
  agents: ['agents'] as const,
  agent: (name: string) => ['agents', name] as const,
  tokens: (name: string) => ['tokens', name] as const,
  /** Prefix of every agent's standing-approval list. */
  allGrants: ['grants'] as const,
  grants: (name: string) => ['grants', name] as const,
  threads: ['threads'] as const,
  threadList: (filters: { agentId?: string | undefined; archived: boolean }) => ['threads', filters] as const,
  /** Prefix of every thread-detail query (invalidates them all). */
  allThreadDetails: ['thread'] as const,
  thread: (id: string) => ['thread', id] as const,
  /** Prefix of every thread's message query. */
  allMessages: ['messages'] as const,
  messages: (id: string) => ['messages', id] as const,
  trace: (id: string) => ['trace', id] as const,
  approvals: ['approvals'] as const,
  approvalList: (status: string) => ['approvals', status] as const,
  /** One approval with its detail (a command, a file list); lists never carry the detail. */
  approval: (id: string) => ['approvals', 'detail', id] as const,
  audit: ['audit'] as const,
  auditList: (tab: 'llm' | 'tools', filters: Record<string, string | undefined>) =>
    ['audit', tab, filters] as const,
  /** Prefix of every usage query (completed turns invalidate them all). */
  usage: ['usage'] as const,
  usageFor: (range: string, agent: string | null) => ['usage', range, agent] as const,
  /** Prefix of every activity query. */
  activity: ['activity'] as const,
  activityFor: (agent: string | null, limit: number) => ['activity', agent, limit] as const,
  credits: ['credits'] as const,
};
