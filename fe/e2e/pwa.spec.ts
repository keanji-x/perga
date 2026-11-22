import { mkdir } from 'node:fs/promises';
import { chromium, expect, test } from '@playwright/test';

test('PWA metadata stays valid at the entry point and deep links', async ({ page, context }) => {
  // Installation is available before login; no live kernel is needed for this check.
  await context.route(/^https?:\/\/[^/]+\/api\//, route => route.fulfill({ status: 401, body: '{}' }));
  for (const path of ['/', '/settings/appearance', '/track/pwa-check']) {
    await page.goto(path);
    await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
    const link = page.locator('link[rel="manifest"]');
    await expect(link).toHaveAttribute('href', '/manifest.webmanifest');
    const cdp = await context.newCDPSession(page);
    try {
      const manifest = await cdp.send('Page.getAppManifest');
      expect(manifest.errors).toEqual([]);
      expect(JSON.parse(manifest.data ?? '') as unknown).toMatchObject({
        id: '/', start_url: '/', scope: '/',
        name: 'Perga', short_name: 'Perga', display: 'standalone',
        icons: expect.arrayContaining([
          expect.objectContaining({ sizes: '192x192', type: 'image/png' }),
          expect.objectContaining({ sizes: '512x512', type: 'image/png' }),
        ]),
      });
      await expect.poll(async () => (await cdp.send('Page.getInstallabilityErrors')).installabilityErrors).toEqual([]);
    } finally {
      await cdp.detach();
    }
  }
});

test('Chrome installs and launches Perga in a standalone window', async ({ baseURL }, testInfo) => {
  // PWA installation needs a regular profile and full Chromium, not an incognito
  // context or the headless shell used for ordinary browser tests.
  // Chromium writes installed-app launchers and shortcuts under HOME (Desktop)
  // and the XDG data, config and cache directories; keep them all in the test
  // output rather than the host's home directory.
  const home = testInfo.outputPath('home');
  await mkdir(testInfo.outputPath('home', 'Desktop'), { recursive: true });
  const context = await chromium.launchPersistentContext(testInfo.outputPath('profile'), {
    channel: 'chromium',
    env: {
      ...process.env,
      HOME: home,
      XDG_DATA_HOME: testInfo.outputPath('xdg-data'),
      XDG_CONFIG_HOME: testInfo.outputPath('xdg-config'),
      XDG_CACHE_HOME: testInfo.outputPath('xdg-cache'),
    },
  });
  try {
    await context.route(/^https?:\/\/[^/]+\/api\//, route => route.fulfill({ status: 401, body: '{}' }));
    const page = await context.newPage();
    const manifestId = new URL('/', baseURL).href;
    await page.goto(manifestId);
    await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
    const cdp = await context.newCDPSession(page);
    await cdp.send('PWA.install', { manifestId, installUrlOrBundleUrl: manifestId });
    try {
      // CDP installation defaults the user preference to a browser tab, unlike
      // Chrome's install dialog. Select the app-window preference explicitly.
      // The separate metadata check pins the manifest's own standalone default.
      await cdp.send('PWA.changeAppUserSettings', { manifestId, displayMode: 'standalone' });
      const appPromise = context.waitForEvent('page');
      await cdp.send('PWA.launch', { manifestId });
      const app = await appPromise;
      await expect(app).toHaveURL(manifestId);
      await expect(app.getByRole('heading', { name: 'Sign in' })).toBeVisible();
      expect(await app.evaluate(() => matchMedia('(display-mode: standalone)').matches)).toBe(true);
      await app.goto(new URL('settings/appearance', manifestId).href);
      await app.reload();
      await expect(app.getByRole('heading', { name: 'Sign in' })).toBeVisible();
      expect(await app.evaluate(() => matchMedia('(display-mode: standalone)').matches)).toBe(true);
      await testInfo.attach('installed-window', { body: await app.screenshot(), contentType: 'image/png' });
    } finally {
      await cdp.send('PWA.uninstall', { manifestId });
    }
  } finally {
    await context.close();
  }
});
