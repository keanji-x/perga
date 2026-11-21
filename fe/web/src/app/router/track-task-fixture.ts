import { fixtureSnapshot, fixtureTrack } from './session-contract-fixture.ts';
import type { ApiRequest, ApiTransportPort, ApiTransportResponse } from '../../../../core/api/types.ts';
import { taskDtoSchema } from '../../../../core/api/perga-v1.ts';
import { withReadReceipts } from '../providers/read-receipts-fixture.tsx';

/** A read-only Track with one failed task, as scripted HTTP snapshots. Any write is a test failure. */
export function createTrackTaskFixture() {
  const taskKey = 'calculation-example';
  const goal = 'Calculate six times seven and explain the result.';
  const task = taskDtoSchema.parse({ id: 'task', trackId: 'w1', blockId: 'calculation-task', taskKey,
    kind: 'agent', status: 'failed', runCount: 1, statusDetail: 'The execution reported failure.',
    createdAtMs: 1788600000000, updatedAtMs: 1788600060000, finishedAtMs: 1788600060000 });
  const requests: ApiRequest[] = [];
  const area = { id: 'c1', name: 'Work', color: '#123456', sortOrder: 1, kind: 'user', createdAtMs: 1, updatedAtMs: 1 };
  const track = fixtureTrack('w1', { areaId: 'c1', title: 'The calculation', cwd: '/tmp', sortOrder: 1, closedAtMs: null, createdAtMs: 1, updatedAtMs: 2 });
  const declaration = { id: 'calculation-task', rev: 1, kind: 'task', payload: {
    key: taskKey, kind: 'codex', declared_by: 'user', ready: true, goal,
  } };
  const reportCard = { id: 'report', track_id: 'w1', title: null, kind: 'track-report', sort: 1, deletable: false,
    created_at: 1, updated_at: 2, payload: { schemaVersion: 3, docRev: 7, summary: '', body: '', blocks: [declaration] } };
  const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body: JSON.parse(JSON.stringify(body)) });
  const transport: ApiTransportPort = { send(request) {
    requests.push(request);
    if (request.method !== 'GET') throw new Error(`Unexpected write: ${request.method} ${request.path}`);
    if (request.path === '/api/v1/areas') return Promise.resolve(ok([area]));
    if (request.path === '/api/v1/areas/c1/tracks?includeClosed=true') return Promise.resolve(ok([track]));
    if (request.path === '/api/v1/tracks/w1') return Promise.resolve(ok({ ...fixtureSnapshot(track, [], { docRev: reportCard.payload.docRev, summary: reportCard.payload.summary, leadingComment: '', blocks: reportCard.payload.blocks.map(block => ({ ...block, markdown: '' })) }), tasks: [task] }));
    if (request.path === '/api/v1/tracks/w1/report') return Promise.resolve(ok({ ...reportCard.payload, taskDiagnostics: [
      { blockId: declaration.id, key: taskKey, schedulable: true, status: task.status,
        statusDetail: task.statusDetail, workerCardId: null, diagnostics: [] },
    ] }));
    if (request.path === '/api/settings') return Promise.resolve(ok({}));
    return Promise.resolve(ok([]));
  } };
  // The receipt routes are answered first and not recorded: `requests` is this fixture's own traffic.
  return { goal, requests, transport: withReadReceipts(transport) };
}
