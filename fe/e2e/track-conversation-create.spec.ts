import { expect } from '@playwright/test';
import { test } from './helpers/workspace-test.ts';
import { agentSessionDtoSchema, sessionCreationReceiptSchema, sessionItemsPageDtoSchema } from '../core/api/perga-v1.ts';
import { createArea, createTrack } from './helpers/seed.js';

test('adopts a v1 session creation receipt and renders the persisted first input once', async ({ page, request }) => {
  const area = await createArea(request);
  try {
    const track = await createTrack(request, area.id);
    const path = `/api/v1/tracks/${track.id}/sessions`;
    const before = agentSessionDtoSchema.array().parse(await (await request.get(path)).json());
    await page.goto(`/track/${track.id}`);
    await page.getByRole('button', { name: 'New conversation', exact: true }).click();
    const text = 'fake-reply: New session reply';
    const composer = page.getByRole('combobox', { name: 'Message', exact: true });
    await composer.fill(text);
    const created = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname === path);
    await composer.press('Enter');
    const response = await created;
    expect(response.status(), await response.text()).toBe(201);
    const receipt = sessionCreationReceiptSchema.parse(await response.json());
    expect(receipt.session).toMatchObject({ trackId: track.id, isLead: false, provider: 'codex' });
    expect(receipt.firstInput?.sessionId).toBe(receipt.session.id);
    const key = await response.request().headerValue('idempotency-key');
    expect(key).toBeTruthy();
    const replay = await request.post(path, { headers: { 'Idempotency-Key': key! }, data: response.request().postDataJSON() as unknown });
    expect(replay.status(), await replay.text()).toBe(200);
    const replayed = sessionCreationReceiptSchema.parse(await replay.json());
    expect(replayed.session.id).toBe(receipt.session.id);
    expect(replayed.firstInput?.inputId).toBe(receipt.firstInput?.inputId);
    await expect(page.locator('[data-nc-thread]').getByText(text, { exact: true })).toHaveCount(1);
    const read = async () => sessionItemsPageDtoSchema.parse(await (await request.get(`/api/v1/sessions/${receipt.session.id}/items?afterId=0&limit=500`)).json());
    await expect.poll(async () => (await read()).items.filter(item => item.kind === 'user').length).toBe(1);
    expect((await read()).items.find(item => item.kind === 'user')?.payload).toMatchObject({ content: text });
    await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
    const after = agentSessionDtoSchema.array().parse(await (await request.get(path)).json());
    expect(after).toHaveLength(before.length + 1);
    expect(after.some(session => session.id === receipt.session.id)).toBe(true);
  } finally { await request.delete(`/api/v1/areas/${area.id}`); }
});

test('v1 session creation refuses an empty first input and missing Track', async ({ request }) => {
  const area = await createArea(request);
  try {
    const track = await createTrack(request, area.id);
    const noKey = await request.post(`/api/v1/tracks/${track.id}/sessions`, { data: { provider: 'codex', text: 'hello' } });
    expect(noKey.status(), await noKey.text()).toBe(400);
    const blank = await request.post(`/api/v1/tracks/${track.id}/sessions`, { headers: { 'Idempotency-Key': crypto.randomUUID() }, data: { provider: 'codex', text: '   ' } });
    expect(blank.status(), await blank.text()).toBe(400);
    const missing = await request.post('/api/v1/tracks/missing/sessions', { headers: { 'Idempotency-Key': crypto.randomUUID() }, data: { provider: 'codex', text: 'hello' } });
    expect(missing.status(), await missing.text()).toBe(404);
  } finally { await request.delete(`/api/v1/areas/${area.id}`); }
});
