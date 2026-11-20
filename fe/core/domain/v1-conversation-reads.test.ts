import { sessionContextUsage } from './session-context-usage.js';
import { expect, it } from 'vitest';
import { modelCatalogOperation } from './conversation.js';
import { buildConversationTranscript, conversationItemsOperation, conversationRunOperation, conversationRunPhase, conversationRunningTurn, conversationPendingQueue } from './conversation-display.js';

const session = Object.freeze({ id: 'session', trackId: 'track', isLead: true, provider: 'codex', model: 'model',
  nativeThreadId: 'thread', epoch: 1, status: 'idle', contextUsage: { usedTokens: 64_000, windowTokens: 256_000, observedAtMs: 7, ratio: 0.25 }, archivedAtMs: null, createdAtMs: 1, updatedAtMs: 2 });
const page = Object.freeze({ session, activeTurn: null, state: 'idle', readonlyReason: null, canReopen: false, workerPaused: false, lastTurn: null, unknownDetail: null, items: [
  { id: 1, sessionId: session.id, turnId: 'turn', kind: 'assistant_text', payload: { text: 'Actual v1 reply' }, createdAtMs: 2 },
] });

it('projects the real session items page through both transcript readers', () => {
  const display = conversationItemsOperation(session.id, null).responseSchema.parse(page);
  expect(buildConversationTranscript(display.items, null)).toMatchObject([{ author: 'agent', text: 'Actual v1 reply' }]);
});

it('keeps a CLI catalog without a declared default selectable', () => {
  const model = 'gemini-4-neon-high';
  const catalog = modelCatalogOperation({ kind: 'provider', provider: 'antigravity' }).responseSchema.parse({
    version: 'test',
    providers: [{
      provider: 'acp:antigravity', available: true, models: [model],
      modelCatalog: [{ model, displayName: 'Gemini 4 Neon (High)', isDefault: false }],
    }],
  });
  expect(catalog.models).toMatchObject([{ model, display_name: 'Gemini 4 Neon (High)', is_default: false }]);
  expect(catalog.default.model).toBeNull();
  expect(catalog.default_source).toBe('unknown');
});

it('keeps the permission options advertised by the open conversation', () => {
  const permissionModes = [{ id: 'inspect', label: 'Inspect', description: 'Read only', tone: 'neutral', isDefault: true }];
  const operation = conversationRunOperation(session.id);
  expect(operation.responseSchema.parse(page).permissionModes).toBeUndefined();
  const run = operation.responseSchema.parse({ ...page, permissionModes });
  expect(run.permissionModes).toEqual(permissionModes);
});

it('rejects raw rows and the retired conversation page at the production read boundary', () => {
  const operation = conversationItemsOperation(session.id, null);
  const display = operation.responseSchema.parse(page);
  expect(operation.responseSchema.safeParse(display).success).toBe(false);
  expect(operation.responseSchema.safeParse([]).success).toBe(false);
});

it('projects idle, queued, running and unknown states from SessionItemsPageDto only', () => {
  for (const operation of [conversationRunOperation(session.id)]) {
    expect(operation.responseSchema.parse(page)).toMatchObject({ session: { status: 'idle', model: 'model' } });
    expect(conversationRunPhase(operation.responseSchema.parse(page))).toBe('idle');
    const queued = { ...page, state: 'running', session: { ...session, status: 'queued' } };
    expect(conversationRunPhase(operation.responseSchema.parse(queued))).toBe('turn_running');
    const running = { ...page, state: 'running', activeTurn: { id: 'turn', sessionId: session.id, turnIndex: 1, epoch: 1,
      triggerKind: 'user', status: 'running', startedAtMs: 1, completedAtMs: null } };
    expect(conversationRunPhase(operation.responseSchema.parse(running))).toBe('turn_running');
    expect(conversationRunningTurn(operation.responseSchema.parse(running), 100)).toEqual({ turn_id: 'turn', elapsed_ms: 99 });
  }
});


it('formats the reported ratio and preserves usage when the window is unknown', () => {
  for (const operation of [conversationRunOperation(session.id)]) {
    for (const [used, window, ratio, percent] of [[123, 1000, 0.123, 12], [0, 1000, 0, 0],
      [1200, 1000, 1, 100], [123, null, null, null]] as const) {
      expect(sessionContextUsage(operation.responseSchema.parse({ ...page, session: { ...session,
        contextUsage: { usedTokens: used, windowTokens: window, observedAtMs: 7, ratio },
      } }).session)).toEqual({ used_tokens: used, context_window: window, percent, at_ms: 7 });
    }
    expect(sessionContextUsage(operation.responseSchema.parse({ ...page, session: { ...session, contextUsage: null } }).session)).toBeNull();
  }
});

it.each(['idle', 'running'])('retains persisted effort for explicit and default models while %s', status => {
  for (const model of ['model', null]) {
    for (const reasoningEffort of ['high', null, undefined]) {
      expect(conversationRunOperation(session.id).responseSchema.parse({ ...page,
        session: { ...session, model, status, reasoningEffort },
      })).toMatchObject({ session: { model, ...(reasoningEffort === undefined ? {} : { reasoningEffort }) } });
    }
  }
});

it('returns the canonical queue, handoff, receipt and resume facts without a second envelope', () => {
  const value = { ...page,
    queuedMessages: [
      { id: 'second', text: 'second message', queuedAtMs: 9, state: 'waiting' },
      { id: 'first', text: 'first message', queuedAtMs: 8, state: 'unconfirmed' },
    ],
    queueHandoffIds: ['handoff'], canSteer: true,

  };
  const run = conversationRunOperation(session.id).responseSchema.parse(value);
  expect(run).toEqual(value);
  expect(conversationPendingQueue(run)).toEqual([
    { entry_id: 'second', text: 'second message', queued_at_ms: 9, rev: 1, state: 'waiting' },
    { entry_id: 'first', text: 'first message', queued_at_ms: 8, rev: 1, state: 'unconfirmed' },
  ]);
  expect(conversationRunOperation(session.id).responseSchema.safeParse({ ...value, state: undefined }).success).toBe(false);
  expect(conversationRunOperation(session.id).responseSchema.safeParse({ ...value, session: { ...session, contextUsage: undefined } }).success).toBe(false);
});

it('keeps actual terminal session status and computes a nonnegative running clock', () => {
  const operation = conversationRunOperation(session.id);
  for (const status of ['failed', 'exited', 'superseded']) {
    const run = operation.responseSchema.parse({ ...page, session: { ...session, status } });
    expect(run.session.status).toBe(status);
    expect(conversationRunningTurn(run, 1)).toBeNull();
  }
  const run = operation.responseSchema.parse({ ...page, activeTurn: { id: 'turn', sessionId: session.id,
    turnIndex: 1, epoch: 1, triggerKind: 'user', status: 'running', startedAtMs: 100, completedAtMs: null } });
  expect(conversationRunningTurn(run, 10)).toEqual({ turn_id: 'turn', elapsed_ms: 0 });
});

it('preserves worker activity and interrupting phase independently of the session turn', () => {
  const worker = {
    task: { id: 'task', trackId: 'track', blockId: 'block', taskKey: 'inspect', kind: 'agent', status: 'running', createdAtMs: 1, updatedAtMs: 2 },
    attemptId: 'attempt', phase: 'interrupting', nativeSteer: true, pendingApprovals: [], sequence: 7, reset: true,
    text: 'partial reply', textTruncated: false, nextSettings: { version: 1, model: null, reasoningEffort: null, approvalMode: 'ask' },
    canChangeModel: true, reasoningEfforts: ['low'], events: [], messages: [],
  };
  const run = conversationRunOperation(session.id).responseSchema.parse({ ...page, state: 'stopping', worker });
  expect(run.worker).toMatchObject(worker);
  expect(conversationRunPhase(run)).toBe('issuing_interrupt');
  expect(conversationRunningTurn(run, 10)).toBeNull();
});
