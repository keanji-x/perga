import { test as base, type APIRequestContext } from '@playwright/test';
import { loginOperation, sessionIdentitySchema } from '../../core/api/auth.ts';
import { requireOk } from './seed.ts';

type StorageState = Awaited<ReturnType<APIRequestContext['storageState']>>;

/** One confirmed owner session per worker for the live workspace scenarios. */
export const test = base.extend<object, { ownerStorage: StorageState }>({
  ownerStorage: [async ({ playwright }, provide, info) => {
    const username = process.env.FE_E2E_USERNAME;
    const password = process.env.FE_E2E_PASSWORD;
    const baseURL = info.project.use.baseURL;
    if (!username || !password || !baseURL) {
      throw new Error('Set FE_E2E_USERNAME and FE_E2E_PASSWORD for the dedicated Perga test Host.');
    }
    const origin = new URL(baseURL).origin;
    const client = await playwright.request.newContext({ baseURL, extraHTTPHeaders: { origin } });
    let sessionId: string | undefined;
    try {
      const login = loginOperation({ username, password });
      const response = await client.post(login.path, { data: login.body });
      await requireOk(response, 'Sign in to test Host');
      const identity = sessionIdentitySchema.parse(await response.json());
      if (response.headers()['x-perga-session-id'] !== identity.sessionId) throw new Error('Invalid test login session header');
      sessionId = identity.sessionId;
      await provide(await client.storageState());
    } finally {
      try {
        if (sessionId !== undefined) await client.post('/api/v1/auth/logout', { headers: { 'X-Perga-Session-Id': sessionId } });
      } finally { await client.dispose(); }
    }
  }, { scope: 'worker' }],
  storageState: async ({ ownerStorage }, provide) => provide(ownerStorage),
  request: async ({ playwright, baseURL, ownerStorage }, provide) => {
    const request = await playwright.request.newContext({
      baseURL, storageState: ownerStorage, extraHTTPHeaders: { origin: new URL(baseURL!).origin },
    });
    try { await provide(request); }
    finally { await request.dispose(); }
  },
});
