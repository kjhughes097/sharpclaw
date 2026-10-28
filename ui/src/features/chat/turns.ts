import type { MessageDto } from '../../api/types';

/** One tool call of a turn: the request (arguments) paired with its result row by tool-call id. */
export interface ToolItem {
  toolCallId: string;
  name: string;
  arguments: string;
  result: MessageDto | null;
}

/**
 * A user turn: the user message, the tool exchanges it caused, and the final reply, joined by the
 * correlation id (the user message's id). Ordered by the user message's `seq`; a queued second
 * message can have a lower seq than the first turn's tool rows, which is why seq alone won't do.
 */
export interface Turn {
  correlationId: string;
  user: MessageDto | null;
  /** Assistant text that accompanied tool requests (not the final reply). */
  interim: string[];
  tools: ToolItem[];
  reply: MessageDto | null;
  seq: number;
}

export function isToolRequest(m: MessageDto) {
  return m.kind === 'tool_request';
}

export function groupTurns(rows: readonly MessageDto[]): Turn[] {
  const turns = new Map<string, Turn>();
  const sorted = [...rows].sort((a, b) => (a.seq ?? 0) - (b.seq ?? 0));
  const results = new Map<string, MessageDto>();
  for (const row of sorted)
    if (row.kind === 'tool_result' && row.toolCallId)
      results.set(`${row.correlationId}:${row.toolCallId}`, row);

  const turnFor = (key: string, seq: number) => {
    let turn = turns.get(key);
    if (!turn) {
      turn = { correlationId: key, user: null, interim: [], tools: [], reply: null, seq };
      turns.set(key, turn);
    }
    return turn;
  };

  for (const row of sorted) {
    const seq = row.seq ?? 0;
    if (row.role === 'user' && row.kind === 'message') {
      const turn = turnFor(row.id, seq);
      turn.user = row;
      turn.seq = seq;
      continue;
    }
    const key = row.correlationId ?? row.id;
    if (row.kind === 'tool_request') {
      const turn = turnFor(key, seq);
      if (row.content.trim()) turn.interim.push(row.content);
      for (const call of row.toolCalls ?? [])
        turn.tools.push({
          toolCallId: call.id,
          name: call.name,
          arguments: call.arguments,
          result: results.get(`${key}:${call.id}`) ?? null,
        });
    } else if (row.kind === 'message' && row.role === 'assistant') {
      turnFor(key, seq).reply = row;
    }
  }
  return [...turns.values()].sort((a, b) => a.seq - b.seq);
}

const MAX_STRING = 40;
const MAX_SUMMARY = 120;

/** A one-line summary of tool arguments: compact JSON with long strings shortened. */
export function summariseArguments(json: string): string {
  let value: unknown;
  try {
    value = JSON.parse(json);
  } catch {
    return json.length > MAX_SUMMARY ? `${json.slice(0, MAX_SUMMARY - 1)}…` : json;
  }
  const shorten = (v: unknown): unknown => {
    if (typeof v === 'string') return v.length > MAX_STRING ? `${v.slice(0, MAX_STRING - 1)}…` : v;
    if (Array.isArray(v)) return v.map(shorten);
    if (v && typeof v === 'object')
      return Object.fromEntries(Object.entries(v).map(([k, x]) => [k, shorten(x)]));
    return v;
  };
  const compact = JSON.stringify(shorten(value));
  return compact.length > MAX_SUMMARY ? `${compact.slice(0, MAX_SUMMARY - 1)}…` : compact;
}

/** Pretty JSON if it parses, else the text as is. */
export function pretty(text: string): string {
  try {
    return JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    return text;
  }
}
