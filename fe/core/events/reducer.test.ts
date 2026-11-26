import { describe,expect,it } from 'vitest';

import { decodeEventFrame } from './protocol.js';
import { initialEventState,reduceEventFrame } from './reducer.js';

function readyFrame(input: unknown) {
  const decoded = decodeEventFrame(input);
  if (decoded.status !== 'ready') throw new Error('fixture did not decode');
  return decoded.frame;
}

describe('event reducer behavior', () => {
  it('drops future-version frames without advancing the cursor', () => {
    const result = reduceEventFrame(
      { cursor: 9, syncEventVersion: 2 },
      readyFrame({ ev: 'area.deleted', data: { id: 'c1' }, _id: 10, eventVersion: 3 }),
    );
    expect(result.state.cursor).toBe(9);
    expect(result.effects).toEqual([]);
  });

  it('advances cursor before rejecting an in-range malformed event', () => {
    const result = reduceEventFrame(
      { cursor: 9, syncEventVersion: 3 },
      readyFrame({ ev: 'area.deleted', data: {}, _id: 10, eventVersion: 2 }),
    );
    expect(result.state.cursor).toBe(10);
    expect(result.effects).toEqual([{ type: 'persist-cursor', id: 10 }]);
  });

  it('turns control frames into pure cache and reconnect effects', () => {
    const replay = reduceEventFrame(
      { cursor: 20, syncEventVersion: 3 },
      readyFrame({ ev: '_replay_complete', _id: 19 }),
    );
    expect(replay.state.cursor).toBeNull();
    expect(replay.effects).toEqual([
      { type: 'persist-cursor', id: null },
      { type: 'clear-cache' },
      { type: 'reconnect' },
    ]);

    const snapshot = reduceEventFrame(initialEventState(3), readyFrame({ ev: '_snapshot_required' }));
    expect(snapshot.effects).toEqual([
      { type: 'persist-cursor', id: null },
      { type: 'clear-cache' },
      { type: 'reconnect' },
    ]);
  });

  it('converges normally completed replay with one global invalidation', () => {
    const replay = reduceEventFrame(
      { cursor: 20, syncEventVersion: 3 },
      readyFrame({ ev: '_replay_complete', _id: 20 }),
    );
    expect(replay.effects).toEqual([{ type: 'invalidate', keys: null }]);
  });

  it('advances a valid cursor and emits the event invalidation plan', () => {
    const result = reduceEventFrame(
      initialEventState(3),
      readyFrame({ ev: 'area.deleted', data: { id: 'c1' }, _id: 7, eventVersion: 3 }),
    );
    expect(result.state.cursor).toBe(7);
    expect(result.effects).toEqual([
      { type: 'persist-cursor', id: 7 },
      { type: 'write-through', writes: [{ mode: 'area-deleted', areaId: 'c1' }] },
      { type: 'invalidate', keys: [['areas'], ['overlays', 'track']] },
    ]);
  });

  it('never persists missing, non-positive, or non-increasing cursor ids', () => {
    for (const id of [undefined, 0, -1, 8]) {
      const result = reduceEventFrame(
        { cursor: 8, syncEventVersion: 3 },
        readyFrame({ ev: 'area.deleted', data: { id: 'c1' }, _id: id, eventVersion: 3 }),
      );
      expect(result.state.cursor).toBe(8);
      expect(result.effects).toEqual([
        { type: 'write-through', writes: [{ mode: 'area-deleted', areaId: 'c1' }] },
        { type: 'invalidate', keys: [['areas'], ['overlays', 'track']] },
      ]);
    }
    const zeroFromColdStart = reduceEventFrame(
      initialEventState(3),
      readyFrame({ ev: 'area.deleted', data: { id: 'c1' }, _id: 0, eventVersion: 3 }),
    );
    expect(zeroFromColdStart.effects).toEqual([
      { type: 'write-through', writes: [{ mode: 'area-deleted', areaId: 'c1' }] },
      { type: 'invalidate', keys: [['areas'], ['overlays', 'track']] },
    ]);
  });

  it('emits complete event effects in write-through, invalidate, remove order', () => {
    const area = {
      id: 'c1', name: 'new', color: '#abc', sortOrder: 0, folders: [],
      defaultCwd: null, createdAtMs: 1, updatedAtMs: 2,
    };
    const updated = reduceEventFrame(
      initialEventState(3),
      readyFrame({ ev: 'area.updated', data: area, _id: 7, eventVersion: 3 }),
    );
    expect(updated.effects).toEqual([
      { type: 'persist-cursor', id: 7 },
      {
        type: 'write-through',
        writes: [{ key: ['areas'], mode: 'replace-existing-area', value: area }],
      },
      { type: 'invalidate', keys: [['areas']] },
    ]);

    const deleted = reduceEventFrame(
      updated.state,
      readyFrame({ ev: 'track.deleted', data: { id: 'w1', area_id: 'c1' }, _id: 8, eventVersion: 3 }),
    );
    expect(deleted.effects).toEqual([
      { type: 'persist-cursor', id: 8 },
      {
        type: 'invalidate',
        keys: [['tracks', 'c1'], ['overlays', 'track']],
      },
      { type: 'remove', keys: [['track', 'w1'], ['track-views', 'w1']] },
    ]);
  });
});

it('offers only live, compatible file-open intents without changing the replay cursor', () => {
  const state = initialEventState(3, 123);
  const request = { trackId: 'w1', sessionId: 's1', path: 'src/file.ts', line: 15, endLine: null };
  const frame = { type: 'event', event: { ev: 'workspace.file_open_requested', data: request }, meta: { id: 0, eventVersion: 3 } } as const;
  expect(reduceEventFrame(state, frame)).toEqual({ state, effects: [{ type: 'file-open-requested', request }] });
  expect(reduceEventFrame(state, { ...frame, meta: { id: 124, eventVersion: 3 } })).toEqual({ state, effects: [] });
  expect(reduceEventFrame(state, { ...frame, meta: { id: 0, eventVersion: 4 } })).toEqual({ state, effects: [] });
});
