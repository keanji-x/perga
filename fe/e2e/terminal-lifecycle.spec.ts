import { fixtureBootstrap, fixtureTrack, fixtureSnapshot } from '../web/src/app/router/session-contract-fixture.ts';
import { expect, test } from '@playwright/test';

// Browser integration with controlled HTTP responses; no worker is launched.
test('refreshing an exited terminal shows its final state while secondary reads are slow', async ({ page }, testInfo) => {
  const area = { id: 'area-1', name: 'Work', color: '#6a8', sortOrder: 1, folders: [], createdAtMs: 1, updatedAtMs: 1 };
  const track = fixtureTrack('track-1', { areaId: area.id, title: 'Terminal lifecycle' });
  const status = 'exited';
  let releaseSecondaryReads: () => void = () => {};
  const secondaryReads = new Promise<void>((resolve) => { releaseSecondaryReads = resolve; });
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.routeWebSocket('**/api/v1/events**', () => {});
  await page.route(/^http:\/\/[^/]+\/api\//, async (route) => {
    const path = new URL(route.request().url()).pathname;
    let body: unknown;
    switch (path) {
      case '/api/v1/auth/session': body = { sessionId: 'terminal-fixture', userId: 'owner',
        username: 'owner', displayName: 'Owner', role: 'owner', expiresAtMs: 2000000000000, mode: 'owner' }; break;
      case '/api/v1/bootstrap': body = fixtureBootstrap(); break;
      case '/api/v1/areas': body = [area]; break;
      case '/api/v1/areas/area-1/tracks': body = [track]; break;
      case '/api/v1/tracks/track-1':
        body = fixtureSnapshot(track);
        break;
      case '/api/v1/tracks/track-1/terminals':
        body = [{ id: 'card-1', trackId: track.id, title: 'Terminal', cwd: '/tmp', status, createdAtMs: 1 }];
        break;
      case '/api/v1/tracks/track-1/report':
      case '/api/v1/tracks/track-1/sessions':
        await secondaryReads;
        body = path.endsWith('/report') ? fixtureSnapshot(track).report : [];
        break;
      default: body = []; break;
    }
    await route.fulfill({ json: body });
  });
  try {
    await page.goto('/track/track-1?card=card-1');
    await expect(page.getByText('Session exited.')).toBeVisible();
    await expect(page).toHaveURL(/\?card=card-1$/);
    await page.reload();
    await expect(page.getByText('Session exited.')).toBeVisible();
    await expect(page.getByText('Starting terminal…')).toHaveCount(0);
    const terminal = page.locator('[data-nc-terminal-card]');
    await expect(terminal).toHaveCount(1);
    await expect(terminal.locator('[data-nc-activity]')).toHaveCount(0);
    await page.screenshot({ path: testInfo.outputPath('terminal-exited.png'), fullPage: true });
    expect(errors).toEqual([]);
  } finally {
    releaseSecondaryReads();
  }
});
