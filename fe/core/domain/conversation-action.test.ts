import { expect, it } from 'vitest';
import { recoverSessionOperation } from './conversation.js';
import { conversationStopTurn, conversationCommandEnabled, deriveConversationAction } from './conversation-action.js';

const baseline = Object.freeze({ readError: null, readonlyReason: null, canReopen: false, state: 'idle' as const,
  unknownDetail: null, hasAsks: false, localSend: 'idle' as const, sendUnconfirmed: false, actionError: null, workerPaused: false });
const supported = Object.freeze({ canStop: false, manualCompactSupported: true, canResetContext: true });
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
  const capabilities = { ...supported, canStop: state === 'running' };
  expect(conversationCommandEnabled('stop', state, null, unknown, false, capabilities)).toBe(state === 'running');
  expect(conversationCommandEnabled('compact', state, null, unknown, false, capabilities)).toBe(state === 'idle');
  expect(conversationCommandEnabled('reset', state, null, unknown, false, capabilities)).toBe(state === 'idle' || state === 'unknown');
  expect(conversationCommandEnabled('reconnect', state, null, unknown, false, capabilities)).toBe(state === 'unknown');
  for (const command of ['stop', 'compact', 'reset', 'reconnect'] as const) {
    expect(conversationCommandEnabled(command, state, 'archived', unknown, false, capabilities)).toBe(false);
    expect(conversationCommandEnabled(command, state, null, unknown, true, capabilities)).toBe(false);
  }
  expect(conversationCommandEnabled('new', state, 'archived', unknown, true, capabilities)).toBe(true);
});
it('honors unavailable recovery capabilities', () => {
  expect(conversationCommandEnabled('reconnect', 'unknown', null, { ...unknown, canReconnect: false }, false, supported)).toBe(false);
  expect(conversationCommandEnabled('reset', 'unknown', null, { ...unknown, canResetContext: false }, false, supported)).toBe(false);
});

it('uses the unified recovery endpoint for both modes', () => {
  for (const mode of ['reconnect', 'reset_context'] as const) {
    const operation = recoverSessionOperation('session /', mode);
    expect(operation).toMatchObject({ method: 'POST', path: '/api/v1/sessions/session%20%2F/recover', body: { mode } });
    expect(operation.responseSchema.safeParse({ id: 'other', trackId: 't', provider: 'codex', contextUsage: null, createdAtMs: 1, updatedAtMs: 1 }).success).toBe(false);
  }
});

it('uses startup stop identity and denies unsupported Worker context commands', () => {
  const capabilities = { canStop: true, manualCompactSupported: false, canResetContext: false };
  expect(conversationCommandEnabled('stop', 'running', null, null, false, capabilities)).toBe(true);
  for (const state of ['idle', 'unknown'] as const) {
    expect(conversationCommandEnabled('compact', state, null, unknown, false, capabilities)).toBe(false);
    expect(conversationCommandEnabled('reset', state, null, unknown, false, capabilities)).toBe(false);
  }
});

it.each(['queued', 'starting', 'running'])('keeps %s lastTurn identity before activation', status => {
  const turn = { id: 'startup', sessionId: 'a', epoch: 1, turnIndex: 1, triggerKind: 'user', status, startedAtMs: null };
  expect(conversationStopTurn({ activeTurn: null, lastTurn: turn }, null)).toBe('startup');
  expect(conversationStopTurn({ activeTurn: null, lastTurn: null }, 'accepted')).toBe('accepted');
  expect(conversationStopTurn({ activeTurn: null, lastTurn: { ...turn, status: 'completed' } }, 'startup')).toBeNull();
});

it.each(['idle', 'unknown'] as const)('retires skipped accepted turns only on authoritative %s reads', state => {
  const lastTurn = { id: 'turn-2', sessionId: 'a', epoch: 1, turnIndex: 2, triggerKind: 'user', status: 'completed', startedAtMs: null };
  const run = { state, activeTurn: null, lastTurn };
  expect(conversationStopTurn(run, 'turn-1', true)).toBeNull();
  expect(conversationStopTurn(run, 'turn-1', false)).toBe('turn-1');
  expect(conversationStopTurn({ ...run, lastTurn: null }, 'turn-1', true)).toBeNull();
});
it.each(['queued', 'starting', 'running'])('retains %s startup on a post-ACK read', status => {
  const lastTurn = { id: 'turn-1', sessionId: 'a', epoch: 1, turnIndex: 1, triggerKind: 'user', status, startedAtMs: null };
  expect(conversationStopTurn({ state: 'running', activeTurn: null, lastTurn }, 'turn-1', true)).toBe('turn-1');
});
