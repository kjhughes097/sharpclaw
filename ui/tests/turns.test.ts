import { describe, expect, it } from 'vitest';
import { groupTurns, summariseArguments } from '../src/features/chat/turns';
import { fixtures } from './msw';

const m = fixtures.message;

describe('groupTurns', () => {
  it('groups by correlation id and orders by the user message', () => {
    const rows = [
      m({ id: 'u1', seq: 1, content: 'first' }),
      // A second message posted while the first turn ran: its seq is before u1's exchanges.
      m({ id: 'u2', seq: 2, content: 'second', status: 'pending' }),
      m({
        id: 'tr1',
        seq: 3,
        role: 'assistant',
        kind: 'tool_request',
        correlationId: 'u1',
        content: 'Let me look',
        toolCalls: [{ id: 'call_1', name: 'workspace_read', arguments: '{"path":"a.md"}' }],
      }),
      m({
        id: 'res1',
        seq: 4,
        role: 'tool',
        kind: 'tool_result',
        correlationId: 'u1',
        toolCallId: 'call_1',
        toolOk: true,
        content: 'text',
        untrusted: true,
      }),
      m({ id: 'r1', seq: 5, role: 'assistant', correlationId: 'u1', content: 'done' }),
    ];
    const turns = groupTurns(rows);
    expect(turns.map((t) => t.correlationId)).toEqual(['u1', 'u2']);
    const first = turns[0]!;
    expect(first.user?.id).toBe('u1');
    expect(first.reply?.id).toBe('r1');
    expect(first.interim).toEqual(['Let me look']);
    expect(first.tools).toHaveLength(1);
    expect(first.tools[0]?.result?.id).toBe('res1');
    expect(turns[1]?.reply).toBeNull();
  });

  it('keeps a reply whose user message is on an unloaded page', () => {
    const turns = groupTurns([
      m({ id: 'r0', seq: 101, role: 'assistant', correlationId: 'u0', content: 'late' }),
    ]);
    expect(turns[0]?.user).toBeNull();
    expect(turns[0]?.reply?.content).toBe('late');
  });
});

describe('summariseArguments', () => {
  it('compacts JSON and shortens long strings', () => {
    expect(summariseArguments('{\n  "path": "a.md"\n}')).toBe('{"path":"a.md"}');
    const long = summariseArguments(JSON.stringify({ content: 'y'.repeat(500) }));
    expect(long.length).toBeLessThanOrEqual(120);
    expect(long).toContain('…');
  });
  it('falls back to the raw text', () => {
    expect(summariseArguments('not json')).toBe('not json');
  });
});
