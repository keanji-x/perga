import { z } from 'zod';

import { bootstrapDtoSchema, type BootstrapDto } from './perga-v1.js';
import type { ApiOperation } from './types.js';

/** Stable deployment owner identity. */
export const DEPLOYMENT_OWNER_USER_ID = 'local-owner';

export const sessionIdentitySchema = z.strictObject({
  sessionId: z.string().min(1),
  userId: z.string(),
  username: z.string(),
  displayName: z.string(),
  role: z.string(),
  expiresAtMs: z.number().int(),
});
export type SessionIdentity = z.infer<typeof sessionIdentitySchema>;

export const loginRequestSchema = z.strictObject({
  username: z.string(),
  password: z.string(),
});
export type LoginRequest = z.infer<typeof loginRequestSchema>;

export function bootstrapOperation(): ApiOperation<BootstrapDto> {
  return { method: 'GET', path: '/api/v1/bootstrap', responseSchema: bootstrapDtoSchema };
}

export function whoamiOperation(): ApiOperation<SessionIdentity> {
  return {
    method: 'GET', path: '/api/v1/auth/session', responseSchema: sessionIdentitySchema,
    expectedStatuses: [200], redirect: 'error',
  };
}

export function loginOperation(body: LoginRequest): ApiOperation<SessionIdentity> {
  return {
    method: 'POST', path: '/api/v1/auth/login', body, responseSchema: sessionIdentitySchema,
    expectedStatuses: [200], redirect: 'error',
    validateResponse: (identity, response) => response.headers?.['x-perga-session-id'] === identity.sessionId,
  };
}

export function logoutOperation(sessionId?: string): ApiOperation<undefined> {
  return {
    method: 'POST', path: '/api/v1/auth/logout', responseSchema: z.undefined(),
    expectedStatuses: [204], redirect: 'error',
    ...(sessionId === undefined ? {} : { headers: { 'X-Perga-Session-Id': sessionId } }),
  };
}
