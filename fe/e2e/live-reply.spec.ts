import { sessionItemsPageDtoSchema, trackAggregateSnapshotDtoSchema } from '../core/api/perga-v1.ts';
import { expect, test } from '@playwright/test';
import { createArea } from './helpers/seed.js';

test('renders a completed v1 reply once and retains it after reload', async ({ page, request }) => {
  const area = await createArea(request, `Reply lifecycle ${Date.now()}`);
  try {
    const created = await request.post('/api/v1/tracks', { data: {
      areaId: area.id, provider: 'codex', title: 'Reply lifecycle', firstMessage: 'fake-reply: Stored v1 reply',
    } });
    expect(created.status(), await created.text()).toBe(201);
    const snapshot = trackAggregateSnapshotDtoSchema.parse(await created.json());
    const session = snapshot.sessions.find(row => row.isLead);
    if (session === undefined) throw new Error('Missing lead session');
    const read = async () => sessionItemsPageDtoSchema.parse(await (await request.get(`/api/v1/sessions/${session.id}/items?afterId=0&limit=500`)).json());
    await expect.poll(async () => (await read()).items.filter(item => item.kind === 'assistant_text').length).toBe(1);
    const stored = await read();
    expect(stored.activeTurn).toBeNull();
    expect(stored.items.find(item => item.kind === 'assistant_text')?.payload).toMatchObject({ text: 'Stored v1 reply' });
    await page.goto(`/next/track/${snapshot.track.id}`);
    await page.getByRole('button', { name: `Conversation ${session.title}` }).click();
    const reply = page.locator('[data-nc-thread] [data-nc-turn="agent"]');
    await expect(reply).toHaveCount(1);
    await expect(reply).toHaveText('Stored v1 reply');
    await page.reload();
    await expect(reply).toHaveCount(1);
    await expect(reply).toHaveText('Stored v1 reply');
    expect((await read()).items.map(item => item.id)).toEqual(stored.items.map(item => item.id));
  } finally { await request.delete(`/api/v1/areas/${area.id}`); }
});
