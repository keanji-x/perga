import { describe, expect, it } from 'vitest';

import { wireEventSchema, type WireEvent } from '../api/schemas.js';
import { invalidationPlanFor } from './invalidation-plan.js';

function event(value: unknown): WireEvent {
  return value as WireEvent;
}

describe('invalidation plan behavior', () => {
  it('refreshes the settled track\'s report after a git delivery settles', () => {
    const settled = wireEventSchema.parse({
      ev: 'task.git_delivery_settled',
      data: {
        task_id: 'opaque-attempt', idempotency_key: 'opaque-attempt', track_id: 'track-7', card_id: 'worker',
        delivery_id: 'delivery-1', ordinal: 1,
        result: { kind: 'failed', code: 'unresolved', reason: 'probe unknown', retry_allowed: true },
        wake_reason: 'failed',
      },
    });
    expect(invalidationPlanFor(settled)).toEqual({
      invalidate: [['track-report', 'track-7']], remove: [], writeThrough: [],
    });
  });

  it('carries confirmed Area deletion before dependent invalidations', () => {
    expect(invalidationPlanFor({ ev: 'area.deleted', data: { id: 'c1' } })).toEqual({
      invalidate: [['areas'], ['overlays', 'track'], ['track-report']], remove: [],
      writeThrough: [{ mode: 'area-deleted', areaId: 'c1' }],
    });
  });

  it('write-through updates only an existing area before invalidating the list', () => {
    const value = event({ ev: 'area.updated', data: { id: 'c1', name: 'new' } });
    expect(invalidationPlanFor(value)).toEqual({
      invalidate: [['areas']],
      remove: [],
      writeThrough: [{ key: ['areas'], mode: 'replace-existing-area', value: value.data }],
    });
  });

  it('invalidates track projections and pending diagnostics for track updates', () => {
    expect(invalidationPlanFor(event({ ev: 'track.updated', data: { id: 'w1', area_id: 'c1' } }))).toEqual({
      invalidate: [
        ['tracks', 'c1'], ['track', 'w1'], ['track-template', 'w1'],
        ['track-report'],
      ],
      remove: [],
      writeThrough: [],
    });
  });

  it('removes deleted track detail after invalidating its remaining projections', () => {
    expect(invalidationPlanFor(event({ ev: 'track.deleted', data: { id: 'w1', area_id: 'c1' } }))).toEqual({
      invalidate: [
        ['tracks', 'c1'], ['overlays', 'track'], ['track-report'],
      ],
      remove: [['track', 'w1'], ['track-report', 'w1']],
      writeThrough: [],
    });
  });

  it('refreshes the deleted card’s track and diagnostics without removing cached resources', () => {
    expect(invalidationPlanFor(event({ ev: 'card.deleted', data: { id: 'card-1', track_id: 'track-1' } })))
      .toEqual({ invalidate: [['track', 'track-1'], ['track-report']], remove: [], writeThrough: [] });
  });

  it('refreshes pending diagnostics after a task plan mutation', () => {
    expect(invalidationPlanFor(event({
      ev: 'plan.updated',
      data: { track_id: 'w1', changed_keys: ['task-1'], agent_message: 'canceled' },
    }))).toEqual({
      invalidate: [['track-report', 'w1']],
      remove: [],
      writeThrough: [],
    });
  });

  it('does not duplicate the companion event that already invalidates a projection change', () => {
    expect(invalidationPlanFor(event({
      ev: 'plan.updated', data: { track_id: 'w1', changed_keys: ['task-1'] },
    })).invalidate).toEqual([]);
  });

  it('invalidates card mutations immediately without suppression or debounce state', () => {
    expect(invalidationPlanFor(event({ ev: 'card.added', data: { track_id: 'w1' } }))).toEqual({
      invalidate: [
        ['track', 'w1'], ['track-report'],
        ['track', 'w1'],
      ],
      remove: [],
      writeThrough: [],
    });
  });

  it.each(['card.added', 'card.deleted'] as const)(
    'broadly refreshes cross-track card references after %s',
    (ev) => {
      const keys = invalidationPlanFor(event({ ev, data: { track_id: 'target' } })).invalidate;
      expect(keys).toContainEqual(['track-report']);
    },
  );

  /* Keyed BY TRACK: the bare prefix would still satisfy a contains-key assertion while every
   * open track refetched its list on every runtime tick of every other track. */
  it.each([
    ['card.added', { track_id: 'track-1' }],
    ['card.updated', { track_id: 'track-1' }],
    ['harness.phase.changed', { card_id: 'card-1', track_id: 'track-1' }],
    ['harness.user_message.enqueued', { card_id: 'card-1', track_id: 'track-1' }],
    ['worker_session.started', { card_id: 'card-1' }],
    ['worker_session.status_changed', { card_id: 'card-1' }],
    ['worker_session.superseded', { card_id: 'card-1' }],
  ] as const)('keys the track snapshot by its own track for %s', (ev, data) => {
    const keys = invalidationPlanFor(event({ ev, data }), { findTrackOwningCard: () => 'track-1' })
      .invalidate.filter((key) => key[0] === 'track');
    expect(keys.length).toBeGreaterThan(0);
    expect(keys.every(key => key.length === 2 && key[1] === 'track-1')).toBe(true);
  });

  it('resolves runtime projections through card ownership', () => {
    expect(invalidationPlanFor(
      event({ ev: 'worker_session.started', data: { card_id: 'card-1' } }),
      { findTrackOwningCard: () => 'track-1' },
    )).toEqual({
      invalidate: [
        ['track', 'track-1'], ['overlays', 'card'], ['track-report', 'track-1'],
        ['track', 'track-1'],
      ],
      remove: [],
      writeThrough: [],
    });
  });

  /* An unresolvable card falls back to the bare prefix rather than dropping the key, so an open list is never stale forever. */
  it('falls back to the track snapshot prefix when card ownership is unknown', () => {
    expect(invalidationPlanFor(
      event({ ev: 'worker_session.status_changed', data: { card_id: 'card-1' } }),
      { findTrackOwningCard: () => null },
    )).toEqual({
      invalidate: [
        ['overlays', 'card'], ['track-report'],
        ['track'],
      ],
      remove: [],
      writeThrough: [],
    });
  });

  it('silently omits a card-overlay track detail when ownership is unknown', () => {
    expect(invalidationPlanFor(
      event({ ev: 'overlay.set', data: { entity_kind: 'card', entity_id: 'card-1' } }),
      { findTrackOwningCard: () => null },
    )).toEqual({
      invalidate: [['overlays', 'card']],
      remove: [],
      writeThrough: [],
    });
  });

  it('uses track_id, then card ownership, then the broad task-verdict prefix', () => {
    const context = { findTrackOwningCard: (cardId: string) => cardId === 'card-1' ? 'track-1' : null };
    const ev = 'codex.worker_requested';
    expect(invalidationPlanFor(event({ ev, data: { track_id: 'direct' } }), context).invalidate)
      .toEqual([['track-report', 'direct']]);
    expect(invalidationPlanFor(event({ ev, data: { card_id: 'card-1' } }), context).invalidate)
      .toEqual([['track-report', 'track-1']]);
    expect(invalidationPlanFor(event({ ev, data: {} }), context).invalidate)
      .toEqual([['track-report']]);
  });

  it.each(['codex.hook', 'claude.hook'] as const)('plans no cache work for %s without a workspace query', (ev) => {
    const context = { findTrackOwningCard: (cardId: string) => cardId === 'card-1' ? 'track-1' : null };
    expect(invalidationPlanFor(event({ ev, data: { track_id: 'direct' } }), context).invalidate)
      .toEqual([]);
    expect(invalidationPlanFor(event({ ev, data: { card_id: 'card-1' } }), context).invalidate)
      .toEqual([]);
    expect(invalidationPlanFor(event({ ev, data: {} }), context).invalidate)
      .toEqual([]);
  });

  it('invalidates terminal runtime projection through card ownership', () => {
    expect(invalidationPlanFor(
      event({ ev: 'terminal.deleted', data: { card_id: 'card-1' } }),
      { findTrackOwningCard: () => 'track-1' },
    ).invalidate).toEqual([['track-report', 'track-1']]);
  });

  it('plans each harness event against the projections it can change', () => {
    const planned = (ev: WireEvent['ev']) => invalidationPlanFor(
      event({ ev, data: { card_id: 'card-1', track_id: 'track-1' } }),
    ).invalidate;
    expect(planned('harness.item.added')).toEqual([['harness-items', 'card-1']]);
    expect(planned('harness.presentation.changed')).toEqual([
      ['harness-items', 'card-1'], ['planner-run', 'card-1'],
      ['track', 'track-1'],
    ]);
    // The phase event delivers the turn outcome row, which emits no `harness.item.added` of its own.
    expect(planned('harness.phase.changed')).toEqual([
      ['planner-run', 'card-1'], ['harness-items', 'card-1'], ['track', 'track-1'],
    ]);
    expect(invalidationPlanFor(event({ ev: 'harness.phase.changed', data: { card_id: 'card-1', track_id: 'track-1', new_phase: 'turn_completed' } })).invalidate)
      .toContainEqual(['model-catalog', { kind: 'card', cardId: 'card-1' }]);
    expect(planned('harness.transcript.cleared')).toEqual([
      ['harness-items', 'card-1'], ['planner-run', 'card-1'],
      ['track', 'track-1'],
    ]);
    expect(planned('harness.user_message.enqueued')).toEqual([
      ['harness-items', 'card-1'], ['planner-run', 'card-1'],
      ['track', 'track-1'],
    ]);
    // `harness-items` is for `steered` (adds a transcript row) and `restored` (its sweep deletes it again).
    expect(planned('harness.queue.changed')).toEqual([
      ['planner-run', 'card-1'], ['harness-items', 'card-1'],
      ['track', 'track-1'],
    ]);
  });

  it.each(['harness.queue.changed', 'harness.user_message.enqueued', 'harness.transcript.cleared',
    'harness.transcript.rewound', 'harness.phase.changed'] as const)(
    'refreshes only the owning input metadata for %s', ev => {
      const keys = invalidationPlanFor(event({ ev, data: { card_id: 'card-1', track_id: 'track-1' } })).invalidate;
      expect(keys.filter(key => key[0] === 'track'))
        .toEqual([['track', 'track-1']]);
    },
  );

  it('refreshes metadata for persisted user input but not agent/tool/turn items', () => {
    expect(invalidationPlanFor(event({ ev: 'harness.item.added', data: {
      card_id: 'card-1', track_id: 'track-1', item_type: 'userMessage', method: 'item/started',
    } })).invalidate).toEqual([['harness-items', 'card-1']]);
    for (const item_type of ['userMessage', 'user_message', 'agentMessage', 'commandExecution', null]) {
      const keys = invalidationPlanFor(event({ ev: 'harness.item.added', data: {
        card_id: 'card-1', track_id: 'track-1', item_type, method: 'item/completed',
      } })).invalidate;
      expect(keys).toEqual(item_type === 'userMessage' || item_type === 'user_message'
        ? [['harness-items', 'card-1'], ['track', 'track-1']]
        : [['harness-items', 'card-1']]);
    }
  });

  it('refreshes template prerequisites when plugin state changes', () => {
    expect(invalidationPlanFor(event({ ev: 'plugin.state', data: {} })).invalidate).toEqual([['plugins'], ['track-templates']]);
  });

  it('returns an empty plan for explicit no-op policies', () => {
    const empty = { invalidate: [], remove: [], writeThrough: [] };
    expect(invalidationPlanFor({ ev: 'plugin.data.changed', data: { plugin_id: 'calendar' } }).invalidate).toEqual([['plugin-data', 'calendar']]);
    expect(invalidationPlanFor(event({ ev: 'proposal.resolved', data: {} }))).toEqual(empty);
  });

  it('silently ignores an unknown event kind received across versions', () => {
    expect(invalidationPlanFor(event({ ev: 'zzz.unknown', data: {} }))).toEqual({
      invalidate: [],
      remove: [],
      writeThrough: [],
    });
  });
});


it('keeps historical Terminal events opaque and refetches current placement', () => {
  for (const ev of ['card.added', 'card.updated'] as const) {
    const legacy = wireEventSchema.parse({ ev, data: {
      id: 'legacy-terminal', track_id: 'track', kind: 'terminal', sort: 0,
      payload: null, deletable: true, created_at: 1, updated_at: 1,
    } });
    const plan = invalidationPlanFor(legacy);
    expect(plan.invalidate).toContainEqual(['track', 'track']);
    expect(plan.writeThrough).toEqual([]);
  }
});
