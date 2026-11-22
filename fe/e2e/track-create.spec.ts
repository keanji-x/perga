import { expect, test } from '@playwright/test';
import { sessionItemsPageDtoSchema, trackAggregateSnapshotDtoSchema } from '../core/api/perga-v1.ts';
import { createArea } from './helpers/seed.js';

test('creates an untitled Track from the Area composer and persists its first message once', async ({ page, request }) => {
  const area = await createArea(request);
  try {
    const creates: string[] = [];
    page.on('request', pending => {
      if (pending.method() === 'POST' && new URL(pending.url()).pathname === '/api/v1/tracks') creates.push(pending.url());
    });
    await page.goto(`/area/${area.id}/new`);
    const text = `fake-reply: FE e2e ${Date.now()}`;
    await page.getByLabel('What this track should do').fill(text);
    const created = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/v1/tracks');
    await page.getByRole('button', { name: 'Create track', exact: true }).click();
    const response = await created;
    expect(response.status(), await response.text()).toBe(201);
    const body = response.request().postDataJSON() as Record<string, unknown>;
    expect(body).toMatchObject({ areaId: area.id, provider: 'codex', firstMessage: text });
    expect(body).not.toHaveProperty('title');
    const snapshot = trackAggregateSnapshotDtoSchema.parse(await response.json());
    await expect(page).toHaveURL(new RegExp(`/track/${snapshot.track.id}$`));
    await expect(page.locator('[data-nc-page-title]')).toContainText('Untitled track');
    const lead = snapshot.sessions.find(session => session.isLead);
    expect(lead).toBeDefined();
    const read = async () => sessionItemsPageDtoSchema.parse(await (await request.get(`/api/v1/sessions/${lead!.id}/items?afterId=0&limit=500`)).json());
    await expect.poll(async () => (await read()).items.filter(item => item.kind === 'user')).toEqual([
      expect.objectContaining({ sessionId: lead!.id, payload: expect.objectContaining({ content: text }) }),
    ]);
    await page.reload();
    await expect(page.locator('[data-nc-page-title]')).toContainText('Untitled track');
    expect(creates).toHaveLength(1);
    expect((await read()).items.filter(item => item.kind === 'user')).toHaveLength(1);
  } finally { await request.delete(`/api/v1/areas/${area.id}`); }
});
