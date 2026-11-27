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
  mode: z.enum(['owner', 'local']),
});
export type SessionIdentity = z.infer<typeof sessionIdentitySchema>;

export const loginRequestSchema = z.strictObject({
  username: z.string(),
  password: z.string(),
});
export type LoginRequest = z.infer<typeof loginRequestSchema>;

export const authSetupStatusSchema = z.strictObject({
  mode: z.enum(['configured', 'setup_required', 'local_token_required']),
  needsOwnerSetup: z.boolean(),
  canSetupInBrowser: z.boolean(),
  setupBlockedReason: z.enum(['non_loopback', 'existing_data']).nullable(),
  nextStep: z.string().nullable(),
});
export type AuthSetupStatus = z.infer<typeof authSetupStatusSchema>;

export const setupOwnerRequestSchema = z.strictObject({
  username: z.string().nullable(),
  password: z.string().nullable(),
  displayName: z.string().nullable(),
});
export type SetupOwnerRequest = z.infer<typeof setupOwnerRequestSchema>;

export const localTokenExchangeRequestSchema = z.strictObject({ token: z.string().nullable() });
export type LocalTokenExchangeRequest = z.infer<typeof localTokenExchangeRequestSchema>;

export function authSetupStatusOperation(): ApiOperation<AuthSetupStatus> {
  return {
    method: 'GET', path: '/api/v1/auth/setup', responseSchema: authSetupStatusSchema,
    expectedStatuses: [200], redirect: 'error',
  };
}

export function setupOwnerOperation(body: SetupOwnerRequest): ApiOperation<SessionIdentity> {
  return sessionWrite('/api/v1/auth/setup', body, 201, 'owner');
}

export function exchangeLocalTokenOperation(body: LocalTokenExchangeRequest): ApiOperation<SessionIdentity> {
  return sessionWrite('/api/v1/auth/local', body, 200, 'local');
}

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
  return sessionWrite('/api/v1/auth/login', body, 200, 'owner');
}

function sessionWrite(path: string, body: LoginRequest | SetupOwnerRequest | LocalTokenExchangeRequest,
  status: number, mode: SessionIdentity['mode']): ApiOperation<SessionIdentity> {
  return {
    method: 'POST', path, body, responseSchema: sessionIdentitySchema,
    expectedStatuses: [status], redirect: 'error',
    validateResponse: (identity, response) => identity.mode === mode && response.headers?.['x-perga-session-id'] === identity.sessionId,
  };
}

export function logoutOperation(sessionId?: string): ApiOperation<undefined> {
  return {
    method: 'POST', path: '/api/v1/auth/logout', responseSchema: z.undefined(),
    expectedStatuses: [204], redirect: 'error',
    ...(sessionId === undefined ? {} : { headers: { 'X-Perga-Session-Id': sessionId } }),
  };
}
