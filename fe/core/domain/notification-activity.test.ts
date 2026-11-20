import { expect, it } from 'vitest';
import { trackAggregateSnapshotDtoSchema } from '../api/perga-v1.js';
import { snapshotActivityOverlays, trackActivityFrom, overlaysByKindOperation } from './track.js';
import { priorityConversation } from './activity.js';

function snapshot(overrides: object = {}) {
  return trackAggregateSnapshotDtoSchema.parse({
    track: { id: 't', areaId: 'a', title: 'Track', cwd: '/tmp', workspaceDriver: 'direct', sortOrder: 0, createdAtMs: 1, updatedAtMs: 900 },
    report: { docRev: 1, blocks: [] },
    sessions: [
      { id: 'blocked', trackId: 't', provider: 'codex', status: 'unknown', contextUsage: null, createdAtMs: 1, updatedAtMs: 900, lastNotifiableTurnAtMs: 200 },
      { id: 'input', trackId: 't', provider: 'codex', status: 'running', contextUsage: null, createdAtMs: 1, updatedAtMs: 900 },
      { id: 'working', trackId: 't', provider: 'codex', status: 'compacting', contextUsage: null, createdAtMs: 1, updatedAtMs: 900 },
      { id: 'archived', trackId: 't', provider: 'codex', status: 'unknown', contextUsage: null, createdAtMs: 1, updatedAtMs: 900, archivedAtMs: 2, lastNotifiableTurnAtMs: 300 },
      { id: 'worker', trackId: 't', provider: 'codex', boundTaskId: 'task', status: 'idle', contextUsage: null, createdAtMs: 1, updatedAtMs: 900, lastNotifiableTurnAtMs: 400 },
    ],
    pendingApprovals: [{ id: 'blocked-approval', executionId: 'exec', sessionId: 'blocked', title: 'Approve', options: [], createdAtMs: 2 }, { id: 'approval', executionId: 'exec', sessionId: 'input', title: 'Approve', options: [], createdAtMs: 2 }],
    ...overrides,
  });
}
it('projects every active conversation and gives blocked sessions precedence over input', () => {
  const value = snapshot();
  const activity = trackActivityFrom('t', snapshotActivityOverlays(value));
  expect(activity).toMatchObject({ attention: 'failed', working: true, activityAt: null,
    cards: { blocked: 'failed', input: 'input', working: 'working' },
    notifiableSessions: [{ sessionId: 'blocked', lastNotifiableTurnAtMs: 200 }] });
  expect(activity.cards.archived).toBeUndefined();
  const overlay = overlaysByKindOperation('track').responseSchema.parse({ tracks: [{ trackId: 't', areaId: 'a',
    hasBlockedSession: true, pendingApprovalCount: 1, isWorking: true, updatedAtMs: 900,
    notifiableSessions: activity.notifiableSessions }] });
  expect(trackActivityFrom('t', overlay)).toMatchObject({ attention: activity.attention, working: activity.working,
    notifiableSessions: activity.notifiableSessions, activityAt: null, attentionItems: [] });
});
it('retains only conversation watermarks when the track closes', () => {
  const value = snapshot();
  const activity = trackActivityFrom('t', snapshotActivityOverlays({ ...value, track: { ...value.track, closedAtMs: 10 } }));
  expect(activity).toMatchObject({ attention: 'none', working: false, cards: {}, attentionItems: [],
    notifiableSessions: [{ sessionId: 'blocked', lastNotifiableTurnAtMs: 200 }] });
});
it('does not turn historical task failures into track failures', () => {
  const value = snapshot({ sessions: [], pendingApprovals: [], tasks: [{ id: 'task', trackId: 't', blockId: 'block', taskKey: 'task', kind: 'agent', status: 'failed', createdAtMs: 1, updatedAtMs: 2 }] });
  expect(trackActivityFrom('t', snapshotActivityOverlays(value))).toMatchObject({ attention: 'none', working: false, attentionItems: [] });
});
it('chooses blocked, input, unread, then Lead and excludes archived conversations', () => {
  const rows = [{ id: 'lead', isLead: true }, { id: 'unread' }, { id: 'input' }, { id: 'blocked' }, { id: 'archived', archivedAt: 1 }];
  const unread = (row: { id: string }) => row.id === 'unread';
  expect(priorityConversation(rows, { blocked: 'failed', input: 'input' }, unread)?.id).toBe('blocked');
  expect(priorityConversation(rows, { input: 'input', archived: 'failed' }, unread)?.id).toBe('input');
  expect(priorityConversation(rows, {}, unread)?.id).toBe('unread');
  expect(priorityConversation(rows, {}, () => false)?.id).toBe('lead');
});

it.each(['queued', 'running', 'stopping', 'compacting'])('projects %s as working without fabricating unread output', status => {
  const value = snapshot({ pendingApprovals: [], sessions: [{ id: 's', trackId: 't', provider: 'codex', status, contextUsage: null, createdAtMs: 1, updatedAtMs: 900 }] });
  expect(trackActivityFrom('t', snapshotActivityOverlays(value))).toMatchObject({ working: true, attention: 'none', cards: { s: 'working' }, notifiableSessions: [] });
});
it.each(['pending', 'failed', 'unknown'] as const)('projects workspace %s independently of conversation state', workspacePreparation => {
  const value = snapshot({ sessions: [], pendingApprovals: [], workspacePreparation });
  expect(trackActivityFrom('t', snapshotActivityOverlays(value))).toMatchObject({
    attention: workspacePreparation === 'pending' ? 'none' : 'failed', working: workspacePreparation === 'pending',
  });
});
