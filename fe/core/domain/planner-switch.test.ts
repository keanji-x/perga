import { expect, it } from 'vitest';
import { conversationRunOperation } from './conversation-display.js';
import { plannerSwitchOperation, plannerSwitchFailure, PLANNER_SWITCH_COPY, PLANNER_SWITCH_UNCONFIRMED } from './planner-switch.js';

it('patches the lead session and decodes the real Track receipt without a synthetic role revision', () => {
  const operation = plannerSwitchOperation('a/b', 'session-new');
  expect(operation).toMatchObject({
    method: 'PATCH', path: '/api/v1/tracks/a%2Fb', body: { leadSessionId: 'session-new' },
  });
  expect(operation.responseSchema.parse({ id: 'a/b', areaId: 'area', leadSessionId: 'session-new',
    createdAtMs: 1, updatedAtMs: 2 })).toMatchObject({ id: 'a/b', leadSessionId: 'session-new' });
  expect(operation.responseSchema.safeParse({ track_id: 'a/b', planner_card_id: 'session-new',
    demoted_card_id: 'session-old', planner_revision: 8 }).success).toBe(false);
});

it('classifies all eleven protocol refusals with paired copy and blocking card', () => {
  expect(Object.keys(PLANNER_SWITCH_COPY)).toHaveLength(11);
  for (const [code, copy] of Object.entries(PLANNER_SWITCH_COPY)) {
    expect(copy.zh).toMatch(/[\u4e00-\u9fff]/u);
    expect(copy.en).toMatch(/[a-z]/u);
    expect(Object.isFrozen(copy)).toBe(true);
    expect(plannerSwitchFailure({ kind: 'http', status: code === 'planner_user_required' ? 403 : code === 'planner_target_invalid' ? 400 : 409,
      code, message: 'wire copy', body: { card_id: 'blocked' } })).toEqual({ message: copy.en, cardId: 'blocked', unconfirmed: false });
  }
});

it('keeps unknown refusal text but never mistakes uncertain outcomes for refusal', () => {
  expect(plannerSwitchFailure({ kind: 'http', status: 409, code: 'future', message: 'Server reason' }).message).toBe('Server reason');
  for (const failure of [null, { kind: 'transport', message: 'lost' }, { kind: 'decode', message: 'invalid' },
    { kind: 'http', status: 500, code: 'planner_changed', message: 'server' }] as const) {
    expect(plannerSwitchFailure(failure)).toEqual({ message: PLANNER_SWITCH_UNCONFIRMED.en, cardId: null, unconfirmed: true });
  }
});

it.each([true, false])('v1 run controls reflect session capabilities regardless of lead role (%s)', (isLead) => {
  const page = { items: [], activeTurn: null, state: 'idle', readonlyReason: null, canReopen: false, workerPaused: false, lastTurn: null, unknownDetail: null,
    session: { id: 'session', trackId: 'track', isLead, provider: 'codex', model: 'model',
      contextUsage: null, createdAtMs: 1, updatedAtMs: 2 } };
  for (const operation of [conversationRunOperation('session')]) {
    expect(operation.responseSchema.parse(page)).toMatchObject({
      session: { model: 'model' },
    });
  }
});
