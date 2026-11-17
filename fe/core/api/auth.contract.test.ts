import { describe, expect, expectTypeOf, it } from 'vitest';
import type { z } from 'zod';

import {
  bootstrapOperation,
  loginRequestSchema,
  loginOperation,
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

  it('freezes the bootstrap and whoami operations on Perga /api/v1/bootstrap', () => {
    expect(bootstrapOperation()).toMatchObject({ method: 'GET', path: '/api/v1/bootstrap' });
    expect(whoamiOperation()).toMatchObject({ method: 'GET', path: '/api/v1/bootstrap?scope=whoami' });
    const parsed = whoamiOperation().responseSchema.parse({
      version: '1.0.0',
      providers: [],
      templates: [],
      areas: [],
    });
    expect(parsed).toEqual({
      userId: 'local-owner',
      displayName: 'Local Operator',
      role: 'owner',
      sessionId: 'perga-1.0.0',
    });
  });

  it.each([
    ['method', 'GET'],
    ['path', '/api/v1/bootstrap?scope=login'],
    ['body', { username: 'u', password: 'p' }],
  ] as const)('freezes the login probe operation: %s', (field, expected) => {
    expect(loginOperation({ username: 'u', password: 'p' })[field]).toEqual(expected);
  });
});
