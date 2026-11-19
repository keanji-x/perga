import type { APIRequestContext, APIResponse } from '@playwright/test';
import { areaDtoSchema, sessionInputReceiptSchema, sessionItemsPageDtoSchema, trackAggregateSnapshotDtoSchema } from '../../core/api/perga-v1.ts';

export async function requireOk(response: APIResponse, operation: string): Promise<void> {
  if (!response.ok()) throw new Error(`${operation} → ${response.status()}: ${await response.text()}`);
}

export async function createArea(request: APIRequestContext, name = `FE e2e area ${Date.now()}`) {
  const response = await request.post('/api/v1/areas', { data: { name, color: '#6688aa' } });
  await requireOk(response, 'createArea');
  return areaDtoSchema.parse(await response.json());
}

export async function createTrack(request: APIRequestContext, areaId: string, title = `FE e2e track ${Date.now()}`) {
  const response = await request.post('/api/v1/tracks', {
    headers: { 'Idempotency-Key': crypto.randomUUID() }, data: { areaId, provider: 'codex', title },
  });
  await requireOk(response, 'createTrack');
  return trackAggregateSnapshotDtoSchema.parse(await response.json()).track;
}

export async function trackSnapshot(request: APIRequestContext, trackId: string) {
  const response = await request.get(`/api/v1/tracks/${trackId}`);
  await requireOk(response, 'trackSnapshot');
  return trackAggregateSnapshotDtoSchema.parse(await response.json());
}

export async function activatePlanner(request: APIRequestContext, trackId: string): Promise<void> {
  const snapshot = await trackSnapshot(request, trackId);
  const session = snapshot.sessions.find(row => row.isLead);
  if (session === undefined) throw new Error('Track has no lead session');
  const response = await request.post(`/api/v1/sessions/${session.id}/messages`, {
    headers: { 'Idempotency-Key': crypto.randomUUID() }, data: { content: 'fake-reply: Stop race prepared' },
  });
  await requireOk(response, 'activatePlanner');
  sessionInputReceiptSchema.parse(await response.json());
  const deadline = Date.now() + 20_000;
  while (Date.now() < deadline) {
    const read = await request.get(`/api/v1/sessions/${session.id}/items?afterId=0&limit=500`);
    await requireOk(read, 'sessionItems');
    const page = sessionItemsPageDtoSchema.parse(await read.json());
    if (page.session.status === 'idle' && page.activeTurn === null && page.items.some(item => item.kind === 'assistant_text')) return;
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  throw new Error('First input did not complete');
}
