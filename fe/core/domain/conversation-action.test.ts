import { expect, it } from 'vitest';
import { recoverSessionOperation } from './conversation.js';
import { conversationCommandEnabled, deriveConversationAction } from './conversation-action.js';

const baseline = Object.freeze({ readError: null, readonlyReason: null, canReopen: false, state: 'idle' as const,
  unknownDetail: null, hasAsks: false, localSend: 'idle' as const, sendUnconfirmed: false, actionError: null, workerPaused: false });
const unknown = Object.freeze({ reason: 'stream_lost', detail: '连接已断开', canReconnect: true,
  canResetContext: true, recovering: false, lastError: null });
it('selects exactly one action in strict priority order', () => {
  let input: Parameters<typeof deriveConversationAction>[0] = { ...baseline, readError: '同步失败', readonlyReason: 'archived',
    state: 'unknown', unknownDetail: unknown, hasAsks: true, localSend: 'failed', actionError: '操作失败', workerPaused: true };
  expect(deriveConversationAction(input)?.kind).toBe('readError');
  input = { ...input, readError: null };
  expect(deriveConversationAction(input)).toEqual({ kind: 'readonly', reason: 'archived', canReopen: false });
  input = { ...input, readonlyReason: null };
  expect(deriveConversationAction(input)).toEqual({ kind: 'unknown', detail: unknown });
  input = { ...input, state: 'idle' };
  expect(deriveConversationAction(input)?.kind).toBe('ask');
  input = { ...input, hasAsks: false };
  expect(deriveConversationAction(input)?.kind).toBe('sendFailed');
  input = { ...input, localSend: 'idle' };
  expect(deriveConversationAction(input)?.kind).toBe('actionError');
  input = { ...input, actionError: null };
  expect(deriveConversationAction(input)?.kind).toBe('workerPaused');
  expect(deriveConversationAction(baseline)).toBeNull();
});
it.each(['idle', 'running', 'stopping', 'compacting', 'unknown'] as const)('gates commands against explicit %s state', state => {
  expect(conversationCommandEnabled('stop', state, null, unknown, false)).toBe(state === 'running');
  expect(conversationCommandEnabled('compact', state, null, unknown, false)).toBe(state === 'idle');
  expect(conversationCommandEnabled('reset', state, null, unknown, false)).toBe(state === 'idle' || state === 'unknown');
  expect(conversationCommandEnabled('reconnect', state, null, unknown, false)).toBe(state === 'unknown');
  for (const command of ['stop', 'compact', 'reset', 'reconnect'] as const) {
    expect(conversationCommandEnabled(command, state, 'archived', unknown, false)).toBe(false);
    expect(conversationCommandEnabled(command, state, null, unknown, true)).toBe(false);
  }
  expect(conversationCommandEnabled('new', state, 'archived', unknown, true)).toBe(true);
});
it('honors unavailable recovery capabilities', () => {
  expect(conversationCommandEnabled('reconnect', 'unknown', null, { ...unknown, canReconnect: false }, false)).toBe(false);
  expect(conversationCommandEnabled('reset', 'unknown', null, { ...unknown, canResetContext: false }, false)).toBe(false);
});

it('uses the unified recovery endpoint for both modes', () => {
  for (const mode of ['reconnect', 'reset_context'] as const) {
    const operation = recoverSessionOperation('session /', mode);
    expect(operation).toMatchObject({ method: 'POST', path: '/api/v1/sessions/session%20%2F/recover', body: { mode } });
    expect(operation.responseSchema.safeParse({ id: 'other', trackId: 't', provider: 'codex', contextUsage: null, createdAtMs: 1, updatedAtMs: 1 }).success).toBe(false);
  }
});
