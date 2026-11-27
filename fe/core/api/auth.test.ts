import { describe, expect, it } from 'vitest';

import { exchangeLocalTokenOperation, loginOperation, logoutOperation, sessionIdentitySchema, setupOwnerOperation } from './auth.js';
import { performApiRequest } from './client.js';

const identity = Object.freeze({
  userId: 'owner', username: 'owner', displayName: 'Owner', role: 'owner',
  sessionId: 'session-1', expiresAtMs: 2_000_000_000_000, mode: 'owner',
});

describe('core/api auth behavior', () => {
  it('rejects incomplete and unknown session identity fields', () => {
    expect(sessionIdentitySchema.safeParse({ userId: 'owner' }).success).toBe(false);
    expect(sessionIdentitySchema.safeParse({ ...identity, extra: 1 }).success).toBe(false);
  });

  it.each([[200, 'session-1', 'ready'], [200, 'other', 'failed'], [200, undefined, 'failed'], [201, 'session-1', 'failed']])(
    'verifies login HTTP status %i and session header %s', async (status, sessionId, expected) => {
      const headers: Record<string, string> = sessionId === undefined ? {} : { 'x-perga-session-id': sessionId };
      const result = await performApiRequest({ send: () => Promise.resolve({
        status, statusText: 'OK', body: identity, headers,
      }) }, loginOperation({ username: 'owner', password: 'test-password' }));
      expect(result.status).toBe(expected);
      if (result.status === 'ready') expect(result.value).toEqual(identity);
      else expect(result.error.kind).toBe('decode');
    },
  );

  it.each([
    { mode: 'owner', status: 201, wrongMode: 'local', wrongStatus: 200,
      operation: setupOwnerOperation({ username: 'owner', password: 'sample password', displayName: null }) },
    { mode: 'local', status: 200, wrongMode: 'owner', wrongStatus: 201,
      operation: exchangeLocalTokenOperation({ token: 'token' }) },
  ])('verifies the $mode session status, mode and header', async ({ mode, status, wrongMode, wrongStatus, operation }) => {
    const send = (status: number, mode: string, sessionId: string | undefined) => {
      const headers: Record<string, string> = sessionId === undefined ? {} : { 'x-perga-session-id': sessionId };
      return { send: () => Promise.resolve({ status, statusText: '', body: { ...identity, mode }, headers }) };
    };
    const id = identity.sessionId;
    const failed = { status: 'failed', error: { kind: 'decode' } };
    expect(await performApiRequest(send(status, mode, id), operation)).toMatchObject({ status: 'ready' });
    expect(await performApiRequest(send(status, wrongMode, id), operation)).toMatchObject(failed);
    expect(await performApiRequest(send(wrongStatus, mode, id), operation)).toMatchObject(failed);
    expect(await performApiRequest(send(status, mode, undefined), operation)).toMatchObject(failed);
    expect(await performApiRequest(send(status, mode, 'another-session'), operation)).toMatchObject(failed);
  });

  it('requires logout to return 204', async () => {
    const send = (status: number) => ({ send: () => Promise.resolve({ status, statusText: '', body: undefined }) });
    expect(await performApiRequest(send(204), logoutOperation())).toEqual({ status: 'ready', value: undefined });
    expect(await performApiRequest(send(200), logoutOperation())).toMatchObject({ status: 'failed', error: { kind: 'decode' } });
  });
});
