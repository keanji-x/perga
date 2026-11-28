import { mkdir } from 'node:fs/promises';
import { chromium, expect, test, type BrowserContext } from '@playwright/test';
import { PNG } from 'pngjs';
import { authSetupStatusOperation, type AuthSetupStatus } from '../core/api/auth.ts';

async function mockSignedOut(context: BrowserContext) {
  const setupPath = authSetupStatusOperation().path;
  const setup: AuthSetupStatus = {
    mode: 'configured', needsOwnerSetup: false, canSetupInBrowser: false, setupBlockedReason: null, nextStep: null,
  };
  await context.route(/^https?:\/\/[^/]+\/api\//, route => route.fulfill({ status: 401, json: {} }));
  await context.route(url => url.pathname === setupPath, route => route.fulfill({ json: setup }));
}

test('PWA metadata stays valid at the entry point and deep links', async ({ page, context }) => {
  // Installation is available before login; no live kernel is needed for this check.
  await mockSignedOut(context);
  for (const path of ['/', '/settings/appearance', '/track/pwa-check']) {
    await page.goto(path);
    await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();
    const link = page.locator('link[rel="manifest"]');
    await expect(link).toHaveAttribute('href', '/manifest.webmanifest');
    const appleTouchIcon = page.locator('link[rel="apple-touch-icon"]');
    await expect(appleTouchIcon).toHaveAttribute('sizes', '180x180');
    await expect(appleTouchIcon).toHaveAttribute('href', '/icons/apple-touch-icon.png');
    const cdp = await context.newCDPSession(page);
    try {
      const manifest = await cdp.send('Page.getAppManifest');
      expect(manifest.errors).toEqual([]);
      expect(JSON.parse(manifest.data ?? '') as unknown).toMatchObject({
        id: '/', start_url: '/', scope: '/',
        name: 'Perga', short_name: 'Perga', display: 'standalone',
        icons: expect.arrayContaining([
          { src: 'icons/perga-192.png', sizes: '192x192', type: 'image/png', purpose: 'any' },
          { src: 'icons/perga-512.png', sizes: '512x512', type: 'image/png', purpose: 'any' },
          { src: 'icons/perga-maskable-192.png', sizes: '192x192', type: 'image/png', purpose: 'maskable' },
          { src: 'icons/perga-maskable-512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
        ]),
      });
      await expect.poll(async () => (await cdp.send('Page.getInstallabilityErrors')).installabilityErrors).toEqual([]);
    } finally {
      await cdp.detach();
    }
  }
});

test('mobile startup keeps the loading announcement accessible without painting it', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  // Leave the document and stylesheet intact while holding the app before mount.
  await page.route('**/*', route => route.request().resourceType() === 'script' ? route.abort() : route.continue());
  for (const colorScheme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme });
    await page.goto('/');
    const status = page.getByRole('status');
    await expect(status).toHaveText('正在加载工作区…');
    await expect(status).toHaveCSS('clip-path', 'inset(50%)');
    await expect(status).toHaveCSS('width', '1px');
    await expect(status).toHaveCSS('height', '1px');
    const screenshot = await page.screenshot({ clip: { x: 0, y: 0, width: 160, height: 72 } });
    const { data } = PNG.sync.read(screenshot);
    const background = Buffer.from(colorScheme === 'light' ? [243, 242, 238, 255] : [27, 33, 39, 255]);
    expect(data.equals(Buffer.alloc(data.length, background))).toBe(true);
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
    await mockSignedOut(context);
    const page = await context.newPage();
    const manifestId = new URL('/', baseURL).href;
    await page.goto(manifestId);
    await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();
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
      await expect(app.getByRole('button', { name: 'Sign in' })).toBeVisible();
      expect(await app.evaluate(() => matchMedia('(display-mode: standalone)').matches)).toBe(true);
      await app.goto(new URL('settings/appearance', manifestId).href);
      await app.reload();
      await expect(app.getByRole('button', { name: 'Sign in' })).toBeVisible();
      expect(await app.evaluate(() => matchMedia('(display-mode: standalone)').matches)).toBe(true);
      await testInfo.attach('installed-window', { body: await app.screenshot(), contentType: 'image/png' });
    } finally {
      await cdp.send('PWA.uninstall', { manifestId });
    }
  } finally {
    await context.close();
  }
});
