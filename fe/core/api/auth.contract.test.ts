import { describe, expect, expectTypeOf, it } from 'vitest';
import type { z } from 'zod';

import {
  bootstrapOperation,
  loginRequestSchema,
  loginOperation,
  logoutOperation,
  sessionIdentitySchema,
  whoamiOperation,
} from './auth.js';
import type { LoginRequest, SessionIdentity } from './auth.js';

describe('core/api auth contract', () => {
  it('pins the session request and response schema types bidirectionally', () => {
    expectTypeOf<z.infer<typeof loginRequestSchema>>().toEqualTypeOf<LoginRequest>();
    expectTypeOf<z.infer<typeof sessionIdentitySchema>>().toEqualTypeOf<SessionIdentity>();

    const compileOnly = false as boolean;
    if (compileOnly) {
      // @ts-expect-error -- every session identity field is required by the wire contract.
      const incomplete: SessionIdentity = { userId: 'owner' };
      void incomplete;
    }
  });

  it('keeps bootstrap and session discovery on separate endpoints', () => {
    expect(bootstrapOperation()).toMatchObject({ method: 'GET', path: '/api/v1/bootstrap' });
    expect(whoamiOperation()).toMatchObject({ method: 'GET', path: '/api/v1/auth/session', expectedStatuses: [200] });
    expect(whoamiOperation().responseSchema.safeParse({ version: '1.0.0', providers: [], templates: [], areas: [] }).success)
      .toBe(false);
  });

  it('publishes the exact login and guarded logout requests', () => {
    const body = { username: 'u', password: 'p' };
    expect(loginOperation(body)).toMatchObject({ method: 'POST', path: '/api/v1/auth/login', body, expectedStatuses: [200] });
    expect(logoutOperation('aus_test')).toMatchObject({
      method: 'POST', path: '/api/v1/auth/logout', headers: { 'X-Perga-Session-Id': 'aus_test' }, expectedStatuses: [204],
    });
    expect(logoutOperation()).not.toHaveProperty('headers');
  });
});
