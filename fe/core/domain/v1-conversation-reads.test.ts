import { expect, it } from 'vitest';
import { harnessItemsOperation, plannerRunOperation } from './conversation.js';
import { buildConversationTranscript, conversationItemsOperation, conversationRunOperation } from './conversation-display.js';

const session = Object.freeze({ id: 'session', trackId: 'track', isLead: true, provider: 'codex', model: 'model',
  nativeThreadId: 'thread', epoch: 1, status: 'idle', contextUsedTokens: 64_000, contextWindowTokens: 256_000, contextUsageAtMs: 7, archivedAtMs: null, createdAtMs: 1, updatedAtMs: 2 });
const page = Object.freeze({ session, activeTurn: null, unknownInput: null, items: [
  { id: 1, sessionId: session.id, turnId: 'turn', kind: 'assistant_text', payload: { text: 'Actual v1 reply' }, createdAtMs: 2 },
] });

it('projects the real session items page through both transcript readers', () => {
  const display = conversationItemsOperation(session.id, null).responseSchema.parse(page);
  expect(buildConversationTranscript(display.items, null)).toMatchObject([{ author: 'agent', text: 'Actual v1 reply' }]);
  expect(harnessItemsOperation(session.id).responseSchema.parse(page)).toMatchObject([{ id: 1, thread_id: 'thread' }]);
});

it('rejects raw rows and the retired conversation page at the production read boundary', () => {
  const operation = conversationItemsOperation(session.id, null);
  const display = operation.responseSchema.parse(page);
  expect(operation.responseSchema.safeParse(display).success).toBe(false);
  expect(operation.responseSchema.safeParse([]).success).toBe(false);
  expect(harnessItemsOperation(session.id).responseSchema.safeParse([]).success).toBe(false);
});

it('projects idle, queued, running and unknown states from SessionItemsPageDto only', () => {
  for (const operation of [plannerRunOperation(session.id), conversationRunOperation(session.id)]) {
    expect(operation.responseSchema.parse(page)).toMatchObject({ phase: 'idle', model: 'model', permission_mode: null,
      token_usage: { percent: 25 }, recovery: null });
    const queued = { ...page, session: { ...session, status: 'queued' } };
    expect(operation.responseSchema.parse(queued).phase).toBe('issuing_turn');
    const unknown = { ...page, session: { ...session, status: 'unknown' } };
    expect(operation.responseSchema.parse(unknown).blocked_reason).toMatch(/unknown outcome/);
    const running = { ...page, activeTurn: { id: 'turn', sessionId: session.id, turnIndex: 1, epoch: 1,
      triggerKind: 'user', status: 'running', startedAtMs: 1, completedAtMs: null } };
    expect(operation.responseSchema.parse(running)).toMatchObject({ phase: 'turn_running', running_turn: { turn_id: 'turn' } });
    const legacyRun = operation.responseSchema.parse(page);
    expect(operation.responseSchema.safeParse(legacyRun).success).toBe(false);
  }
});


it('projects real context usage without inventing a window or hiding overflow', () => {
  for (const operation of [plannerRunOperation(session.id), conversationRunOperation(session.id)]) {
    for (const [used, window, percent] of [[123, 1000, 12], [0, 1000, 0], [1000, 1000, 100], [1200, 1000, null], [123, null, null], [123, 0, null]] as const) {
      expect(operation.responseSchema.parse({ ...page, session: { ...session,
        contextUsedTokens: used, contextWindowTokens: window, contextUsageAtMs: 7, contextUsageInvalidated: true,
      } })).toMatchObject({ context_usage_invalidated: true,
        token_usage: { used_tokens: used, context_window: window, percent, at_ms: 7 } });
    }
    expect(operation.responseSchema.parse({ ...page, session: { ...session, contextUsedTokens: 0, contextWindowTokens: 1000, contextUsageAtMs: null } }).token_usage).toEqual({ used_tokens: 0, context_window: 1000, percent: 0, at_ms: null });
    for (const contextUsedTokens of [null, undefined]) {
      expect(operation.responseSchema.parse({ ...page, session: { ...session, contextUsedTokens, contextUsageRatio: 0.25 } }).token_usage).toBeNull();
    }
  }
});
