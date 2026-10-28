import { describe, expect, it } from 'vitest';
import { deciderLabel } from '../src/features/approvals/decider';

describe('deciderLabel', () => {
  it('names the user, the admin key, or falls back to decidedBy', () => {
    expect(deciderLabel('user', 'kevin')).toBe('kevin');
    expect(deciderLabel('user', 'admin_key')).toBe('admin key');
    expect(deciderLabel('system', null)).toBe('system');
    expect(deciderLabel(null, null)).toBeNull();
  });
});
