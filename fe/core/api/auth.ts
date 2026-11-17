import { z } from 'zod';

import { bootstrapDtoSchema, type BootstrapDto } from './perga-v1.js';
import type { ApiOperation } from './types.js';

/** Stable deployment owner identity. */
export const DEPLOYMENT_OWNER_USER_ID = 'local-owner';

export const sessionIdentitySchema = z.strictObject({
  userId: z.string(),
  displayName: z.string(),
  role: z.string(),
  sessionId: z.string(),
});
export type SessionIdentity = z.infer<typeof sessionIdentitySchema>;

export const loginRequestSchema = z.strictObject({
  username: z.string(),
  password: z.string(),
});
export type LoginRequest = z.infer<typeof loginRequestSchema>;

const bootstrapIdentitySchema = z.union([
  sessionIdentitySchema,
  bootstrapDtoSchema.transform((bootstrap): SessionIdentity => ({
    userId: DEPLOYMENT_OWNER_USER_ID,
    displayName: 'Local Operator',
    role: 'owner',
    sessionId: `perga-${bootstrap.version}`,
  })),
]);

export function bootstrapOperation(): ApiOperation<BootstrapDto> {
  return { method: 'GET', path: '/api/v1/bootstrap', responseSchema: bootstrapDtoSchema };
}

export function whoamiOperation(): ApiOperation<SessionIdentity> {
  return { method: 'GET', path: '/api/v1/bootstrap?scope=whoami', responseSchema: bootstrapIdentitySchema };
}

export function loginOperation(body: LoginRequest): ApiOperation<SessionIdentity> {
  return { method: 'GET', path: '/api/v1/bootstrap?scope=login', body, responseSchema: bootstrapIdentitySchema };
}
