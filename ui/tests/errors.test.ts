import { describe, expect, it } from 'vitest';
import { ApiError, errorMessage, failReasonMessage, messageForCode } from '../src/api/errors';
import { localId } from '../src/lib/ids';
import { setServerTime } from '../src/lib/time';
import { formatCost } from '../src/lib/time';

describe('error mapping table', () => {
  it('unauthorized', () =>
    expect(messageForCode('unauthorized')).toBe('Your session has ended. Please sign in again.'));
  it('csrf_failed', () =>
    expect(messageForCode('csrf_failed')).toBe('Your session needs refreshing. Please try again.'));
  it('rate_limited uses Retry-After in minutes', () => {
    expect(messageForCode('rate_limited', { retryAfter: 900 })).toBe(
      'Too many attempts. Try again in 15 minutes.',
    );
    expect(messageForCode('rate_limited', { retryAfter: 30 })).toBe(
      'Too many attempts. Try again in 1 minute.',
    );
  });
  it('validation_failed shows the server message', () =>
    expect(errorMessage(new ApiError(400, 'validation_failed', 'title must be 1 to 120 characters.'))).toBe(
      'title must be 1 to 120 characters.',
    ));
  it('conflict is context-specific', () => {
    expect(messageForCode('conflict')).toBe('Already exists.');
    expect(errorMessage(new ApiError(409, 'conflict', 'x'), { conflict: 'Already decided.' })).toBe(
      'Already decided.',
    );
  });
  it('not_found', () => expect(messageForCode('not_found')).toBe('Not found. It may have been removed.'));
  it('agent_disabled', () => expect(messageForCode('agent_disabled')).toBe('This agent is disabled.'));
  it('budget_exceeded names the local reset time', () => {
    setServerTime('2026-10-25T21:30:00Z');
    expect(messageForCode('budget_exceeded')).toMatch(
      /^Daily budget used up\. Resets at \d{1,2}:\d{2}( [AP]M)?\.$/,
    );
  });
  it('context_too_large', () =>
    expect(messageForCode('context_too_large')).toBe(
      "That is too large for the model's context window. Shorten it or start a new chat.",
    ));
  it('turn_too_large', () =>
    expect(messageForCode('turn_too_large')).toBe(
      "The agent's tool results outgrew the model's context window. Ask for a smaller step, or give the agent a model with a larger window.",
    ));
  it('provider_error', () =>
    expect(messageForCode('provider_error')).toBe('The model provider returned an error. Try again.'));
  it('provider_timeout', () =>
    expect(messageForCode('provider_timeout')).toBe('The model provider timed out. Try again.'));
  it('agent_error', () => expect(messageForCode('agent_error')).toBe('The agent hit an internal error.'));
  it('no_approver', () =>
    expect(messageForCode('no_approver')).toBe('An approval was needed but nobody was available.'));
  it('approval_expired', () =>
    expect(messageForCode('approval_expired')).toBe('An approval request expired before it was answered.'));
  it('denied_by_user', () => expect(messageForCode('denied_by_user')).toBe('You denied a tool request.'));
  it('internal_error and unknown API codes', () => {
    expect(messageForCode('internal_error')).toBe('Something went wrong. Try again.');
    expect(messageForCode('weird_code')).toBe('Something went wrong. Try again.');
    expect(errorMessage(new Error('boom'))).toBe('Something went wrong. Try again.');
  });
  it('unknown fail reasons name the code', () => {
    expect(failReasonMessage('loop_limit_exceeded')).toBe(
      'The agent could not complete this turn (loop_limit_exceeded).',
    );
    expect(failReasonMessage('provider_timeout')).toBe('The model provider timed out. Try again.');
  });
});

describe('secure-context free helpers', () => {
  it('ids work without crypto.randomUUID', () => {
    expect(crypto.randomUUID).toBeUndefined();
    const a = localId();
    const b = localId();
    expect(a).not.toBe(b);
  });

  it('formats costs', () => {
    expect(formatCost(0.0042)).toBe('$0.0042');
    expect(formatCost(0.00001)).toBe('<$0.0001');
    expect(formatCost(0)).toBe('$0.0000');
  });
});
