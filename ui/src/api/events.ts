/**
 * Hand-written SSE payload types (3.3 spec section 7). They are checked against the committed
 * fixtures in docs/api-fixtures/events by tests/events.test.ts.
 */

export interface DeltaEvent {
  correlationId: string;
  content: string;
  threadId: string;
}
export interface SnapshotEvent {
  correlationId: string;
  content: string;
  threadId: string;
}
export interface MessageCreatedEvent {
  messageId: string;
  threadId: string;
  correlationId: string;
}
export interface MessageCompletedEvent {
  correlationId: string;
  messageId: string;
  content: string;
  threadId: string;
}
export interface ErrorEvent {
  correlationId: string;
  code: string;
  message: string;
  threadId: string;
}
export interface ToolRequestedEvent {
  correlationId: string;
  toolCallId: string;
  toolName: string;
  threadId: string;
}
export interface ApprovalRequestedEvent {
  approvalId: string;
  correlationId: string;
  toolName: string;
  summary: string;
  risk: string;
  reason: string;
  expiresAt: string;
  threadId: string;
}
export interface ApprovalDecidedEvent {
  approvalId: string;
  correlationId: string;
  status: string;
  decidedBy: string;
  threadId: string;
}
export interface ThreadCreatedEvent {
  threadId: string;
  agentId: string;
}
export interface ThreadUpdatedEvent {
  threadId: string;
  title: string | null;
  archived: boolean;
}
export interface ReadyEvent {
  threadId: string | null;
}
export interface ResetEvent {
  reason: string;
  threadId: string | null;
}

export interface EventPayloads {
  delta: DeltaEvent;
  snapshot: SnapshotEvent;
  'message.created': MessageCreatedEvent;
  'message.completed': MessageCompletedEvent;
  error: ErrorEvent;
  'tool.requested': ToolRequestedEvent;
  'approval.requested': ApprovalRequestedEvent;
  'approval.decided': ApprovalDecidedEvent;
  'thread.created': ThreadCreatedEvent;
  'thread.updated': ThreadUpdatedEvent;
  ready: ReadyEvent;
  reset: ResetEvent;
}

export type EventType = keyof EventPayloads;

export const EVENT_TYPES = [
  'delta',
  'snapshot',
  'message.created',
  'message.completed',
  'error',
  'tool.requested',
  'approval.requested',
  'approval.decided',
  'thread.created',
  'thread.updated',
  'ready',
  'reset',
] as const satisfies readonly EventType[];

/** A parsed event. `id` is set for durable events; `reconnected` marks a `ready` after a reconnect. */
export type StreamEvent = {
  [K in EventType]: { type: K; data: EventPayloads[K]; id: string | null; reconnected?: boolean };
}[EventType];

/** Narrow runtime check used when parsing: the fields every payload of this type must have. */
const REQUIRED: { [K in EventType]: (keyof EventPayloads[K])[] } = {
  delta: ['correlationId', 'content', 'threadId'],
  snapshot: ['correlationId', 'content', 'threadId'],
  'message.created': ['messageId', 'threadId', 'correlationId'],
  'message.completed': ['correlationId', 'messageId', 'content', 'threadId'],
  error: ['correlationId', 'code', 'threadId'],
  'tool.requested': ['correlationId', 'toolCallId', 'toolName', 'threadId'],
  'approval.requested': ['approvalId', 'correlationId', 'toolName', 'risk', 'expiresAt', 'threadId'],
  'approval.decided': ['approvalId', 'correlationId', 'status', 'threadId'],
  'thread.created': ['threadId', 'agentId'],
  'thread.updated': ['threadId', 'archived'],
  ready: ['threadId'],
  reset: ['reason'],
};

export function parseEvent<K extends EventType>(type: K, json: string): EventPayloads[K] | null {
  try {
    const data = JSON.parse(json) as Record<string, unknown>;
    if (typeof data !== 'object' || data === null) return null;
    for (const key of REQUIRED[type]) if (!(key in data)) return null;
    return data as unknown as EventPayloads[K];
  } catch {
    return null;
  }
}
