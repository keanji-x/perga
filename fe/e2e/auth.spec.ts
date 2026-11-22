import { expect, test as base, type Page, type Response } from '@playwright/test';
import { sessionIdentitySchema } from '../core/api/auth.ts';
import { IDB_DB_NAME, PENDING_TRACK_INITIAL_SENDS_KEY, SYNC_CURSOR_KEY } from '../core/keys/storage.ts';
import { authOwner, withAuthBuild, withAuthHost, type AuthBuild, type AuthHost } from './helpers/auth-host.ts';

const test = base.extend<{ authHost: AuthHost; proxied: boolean }, { authBuild: AuthBuild }>({
  proxied: [false, { option: true }],
  authBuild: [async ({ browserName }, provide) => {
    if (browserName !== 'chromium') throw new Error('The auth browser suite uses Chromium.');
    await withAuthBuild(provide);
  }, { scope: 'worker', timeout: 310_000 }],
  authHost: [async ({ authBuild, proxied }, provide) => withAuthHost(authBuild, proxied, provide), { timeout: 60_000 }],
});

const sessionPath = '/api/v1/auth/session';
const loginPath = '/api/v1/auth/login';
const logoutPath = '/api/v1/auth/logout';
const signInHeading = (page: Page) => page.getByRole('heading', { name: 'Sign in' });
const accountMenu = (page: Page) => page.getByRole('button', { name: `Account menu for ${authOwner.displayName}` });

async function submitCredentials(page: Page, username: string, password: string): Promise<Response> {
  await page.getByLabel('Username').fill(username);
  await page.getByLabel('Password').fill(password);
  const login = page.waitForResponse(reply => new URL(reply.url()).pathname === loginPath);
  await page.getByRole('button', { name: 'Sign in' }).click();
  return login;
}

async function signIn(page: Page, signedIn = accountMenu(page)) {
  const login = await submitCredentials(page, authOwner.username, authOwner.password);
  expect(login.status()).toBe(200);
  const sessionId = login.headers()['x-perga-session-id'];
  await expect(signedIn).toBeVisible();
  const session = await page.request.get(new URL(sessionPath, page.url()).href);
  expect(session.status()).toBe(200);
  const identity = sessionIdentitySchema.parse(await session.json());
  expect(sessionId).toBe(identity.sessionId);
  return identity;
}

async function watchEvents(page: Page) {
  const connections: { url: string; status: number }[] = [];
  const closed: { code: number; reason: string }[] = [];
  const eventSockets = new Map<string, string>();
  const cdp = await page.context().newCDPSession(page);
  cdp.on('Network.webSocketCreated', ({ requestId, url }) => {
    if (new URL(url).pathname === '/api/v1/events') eventSockets.set(requestId, url);
  });
  cdp.on('Network.webSocketHandshakeResponseReceived', ({ requestId, response }) => {
    const url = eventSockets.get(requestId);
    if (url !== undefined) connections.push({ url, status: response.status });
  });
  await cdp.send('Network.enable');
  await page.exposeFunction('authTestSocketClosed', (code: number, reason: string) => { closed.push({ code, reason }); });
  await page.addInitScript(() => {
    const observed = window as unknown as {
      authTestSocketClosed(code: number, reason: string): void;
    };
    window.WebSocket = new Proxy(WebSocket, {
      construct(target, args) {
        const socket = Reflect.construct(target, args) as WebSocket;
        if (new URL(socket.url).pathname === '/api/v1/events') {
          socket.addEventListener('close', event => observed.authTestSocketClosed(event.code, event.reason));
        }
        return socket;
      },
    });
  });
  return { connections, closed };
}

test('gates a deep link and synchronizes login and retried logout across tabs', async ({ authHost, page, context }) => {
  const preLoginApi: string[] = [];
  const recordApi = (incoming: { url(): string }) => {
    const path = new URL(incoming.url()).pathname;
    if (path.startsWith('/api/')) preLoginApi.push(path);
  };
  page.on('request', recordApi);
  await page.goto(new URL('/settings', authHost.origin).href);
  await expect(signInHeading(page)).toBeVisible();
  expect(new Set(preLoginApi)).toEqual(new Set([sessionPath]));
  page.off('request', recordApi);
  let navigations = 0;
  page.on('framenavigated', frame => { if (frame === page.mainFrame()) navigations++; });

  const sibling = await context.newPage();
  await sibling.goto(authHost.origin);
  await expect(signInHeading(sibling)).toBeVisible();
  expect((await submitCredentials(page, authOwner.username, 'wrong-password')).status()).toBe(401);
  await expect(page.getByRole('alert')).toHaveText('Wrong username or password.');

  const settings = page.getByRole('dialog', { name: 'Settings' });
  const identity = await signIn(page, settings);
  await settings.getByRole('button', { name: 'Close' }).click();
  await expect(accountMenu(sibling)).toBeVisible();
  const cookies = await context.cookies(authHost.origin);
  expect(cookies).toEqual(expect.arrayContaining([expect.objectContaining({
    name: 'perga_session', httpOnly: true, secure: false, sameSite: 'Lax', path: '/',
  })]));
  expect(await page.evaluate(() => document.cookie)).not.toContain('perga_session');
  await page.goto(authHost.origin);
  await page.reload();
  await expect(accountMenu(page)).toBeVisible();
  expect(sessionIdentitySchema.parse(await (await page.request.get(new URL(sessionPath, authHost.origin).href)).json()))
    .toEqual(identity);

  const keys = { idbName: IDB_DB_NAME, draftKey: PENDING_TRACK_INITIAL_SENDS_KEY, cursorKey: SYNC_CURSOR_KEY };
  await page.evaluate(({ idbName, draftKey, cursorKey }) => new Promise<void>((resolve, reject) => {
    const request = indexedDB.open(idbName, 1);
    request.onerror = () => reject(request.error ?? new Error('Could not open fixture database.'));
    request.onsuccess = () => {
      request.result.close();
      localStorage.setItem(cursorKey, '42');
      sessionStorage.setItem(draftKey, '{}');
      resolve();
    };
  }), keys);

  const sessionIds: (string | null)[] = [];
  page.on('request', request => {
    if (new URL(request.url()).pathname === logoutPath) sessionIds.push(request.headers()['x-perga-session-id'] ?? null);
  });
  await page.route(`**${logoutPath}`, route => route.fulfill({ status: 503, json: {
    code: 'test_failure', error: 'Temporary failure',
  } }), { times: 1 });
  const beforeLogout = navigations;
  await accountMenu(page).click();
  await page.getByRole('menuitem', { name: 'Sign out' }).click();
  await expect(page.getByRole('alert')).toHaveText('Could not confirm sign-out.');
  expect(navigations).toBe(beforeLogout);
  await expect(accountMenu(page)).toHaveCount(0);
  await expect(accountMenu(sibling)).toBeVisible();
  const logout = page.waitForResponse(response => new URL(response.url()).pathname === logoutPath);
  await page.getByRole('button', { name: 'Retry sign out' }).click();
  expect((await logout).status()).toBe(204);
  expect(sessionIds).toEqual([identity.sessionId, identity.sessionId]);
  await expect(signInHeading(page)).toBeVisible();
  await expect(signInHeading(sibling)).toBeVisible();
  expect(await context.cookies(authHost.origin)).toEqual([]);
  await expect.poll(() => page.evaluate(async ({ idbName, draftKey, cursorKey }) => ({
    database: (await indexedDB.databases()).some(database => database.name === idbName),
    cursor: localStorage.getItem(cursorKey),
    draft: sessionStorage.getItem(draftKey),
  }), keys)).toEqual({ database: false, cursor: null, draft: null });
});

test('CLI revocation closes an open Events socket with 1008 and returns to sign-in', async ({ authHost, page }) => {
  const events = await watchEvents(page);
  await page.goto(authHost.origin);
  await signIn(page);
  await expect(page.locator('[data-nc-event-bridge]')).toHaveCount(1, { timeout: 15_000 });
  await expect.poll(() => events.connections).toContainEqual({ url: expect.stringMatching(/^ws:\/\//), status: 101 });
  await expect(page.locator('[data-nc-connection-status="connected"]')).toBeVisible();
  // Let the initial workspace reads finish and isolate WebSocket-driven revocation from 5s system/info polling.
  await page.waitForLoadState('networkidle');
  await page.route('**/api/v1/system/info', route => route.abort());
  await authHost.revokeSessions();
  await expect.poll(() => events.closed).toContainEqual({ code: 1008, reason: 'session_revoked' });
  await expect(signInHeading(page)).toBeVisible();
});

test('shows the server retry countdown while another username can still sign in', async ({ authHost, page }) => {
  await page.goto(authHost.origin);
  for (let count = 0; count < 5; count++) {
    expect((await submitCredentials(page, 'unknown-owner', 'wrong-password')).status()).toBe(401);
    await expect(page.getByRole('alert')).toHaveText('Wrong username or password.');
  }
  const throttled = await submitCredentials(page, 'unknown-owner', 'wrong-password');
  expect(throttled.status()).toBe(429);
  const seconds = Number(await throttled.headerValue('Retry-After'));
  expect(seconds).toBeGreaterThan(0);
  const countdown = page.getByText(/^Try again in \d+ seconds\.$/);
  await expect(countdown).toBeVisible();
  const initialText = await countdown.textContent();
  const shown = Number(initialText?.match(/\d+/)?.[0] ?? 0);
  expect(shown).toBeGreaterThanOrEqual(Math.max(1, seconds - 2));
  expect(shown).toBeLessThanOrEqual(seconds);
  await expect.poll(async () => {
    const current = await countdown.textContent();
    return Number(current?.match(/\d+/)?.[0] ?? shown);
  }).toBeLessThan(shown);
  await signIn(page);
});

test.describe('trusted TLS ingress', () => {
  test.use({ proxied: true, ignoreHTTPSErrors: true });

  test('uses the secure session cookie and WSS, and rejects unverified TLS', async ({ authHost, page, context }) => {
    const events = await watchEvents(page);
    await page.goto(authHost.origin);
    await signIn(page);
    expect(await context.cookies(authHost.origin)).toEqual([expect.objectContaining({
      name: '__Host-perga_session', secure: true, httpOnly: true, sameSite: 'Lax', path: '/',
    })]);
    expect(await page.evaluate(() => document.cookie)).not.toContain('perga_session');
    await expect(page.locator('[data-nc-event-bridge]')).toHaveCount(1, { timeout: 15_000 });
    await expect.poll(() => events.connections).toContainEqual({ url: expect.stringMatching(/^wss:\/\//), status: 101 });
    for (const forwardedProto of [undefined, 'http']) {
      const headers: Record<string, string> = { host: new URL(authHost.origin).host,
        ...(forwardedProto === undefined ? {} : { 'x-forwarded-proto': forwardedProto }) };
      const rejected = await page.request.get(new URL(sessionPath, authHost.hostOrigin).href, { headers });
      expect(rejected.status()).toBe(403);
      expect(await rejected.json()).toMatchObject({ code: 'insecure_transport_required' });
    }
    const insecurePage = await context.newPage();
    await insecurePage.goto(authHost.hostOrigin);
    await expect(insecurePage.getByRole('alert')).toHaveText('An HTTPS connection is required.');
    await expect(insecurePage.getByText('Access Perga over HTTPS using direct TLS or a configured trusted reverse proxy.')).toBeVisible();
  });
});
