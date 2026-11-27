import { authSetupStatusOperation, exchangeLocalTokenOperation, loginOperation, setupOwnerOperation, type SessionIdentity } from '../../../../core/api/auth.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import { ApiError, classifyFailure, readWriteFailure, refusedText, type WriteText } from '../../../../core/domain/failure-class.ts';
import { LOCAL_TOKEN_TEXT, LOGIN_FAILURES, LOGIN_TEXT, LoginThrottledError, OwnerAlreadyConfiguredError, SETUP_FAILURES, SETUP_TEXT } from '../../../../core/domain/login.ts';
import { runOperation } from '../providers/query-operation.ts';

/**
 * Login treats rejected credentials as an expected result and does not broadcast its 401. Any other failure is
 * thrown as the sentence its `LOGIN_FAILURES` class gives it, never as raw transport text.
 */
export async function loginWithTransport(
  transport: ApiTransportPort, username: string, password: string, signal: AbortSignal,
): Promise<SessionIdentity | null> {
  if (username.trim() === '' || password.trim() === '') return null;
  try {
    signal.throwIfAborted();
    const identity = await runOperation(transport, { ...loginOperation({ username, password }), signal }, undefined);
    signal.throwIfAborted();
    return identity;
  } catch (cause: unknown) {
    if (!(cause instanceof ApiError)) throw cause;
    const failure = cause.failure;
    if (failure.kind === 'http' && failure.code === 'login_throttled' && failure.status === 429 &&
      failure.retryAfterSeconds !== undefined) throw new LoginThrottledError(failure.retryAfterSeconds);
    const kind = classifyFailure(failure, LOGIN_FAILURES);
    if (kind === 'credentials') return null;
    if (kind !== 'refused') throw new Error(LOGIN_TEXT.unknown, { cause });
    const message = refusedText(failure, LOGIN_TEXT.refused);
    const nextStep = failure.kind === 'http' ? failure.nextStep : undefined;
    throw new Error(nextStep ? `${message} ${nextStep}` : message, { cause });
  }
}

export function fetchAuthSetupStatus(transport: ApiTransportPort, signal: AbortSignal) {
  return runOperation(transport, { ...authSetupStatusOperation(), signal }, undefined);
}

export function setupOwnerWithTransport(
  transport: ApiTransportPort, username: string, password: string, signal: AbortSignal,
): Promise<SessionIdentity> {
  return runSetupWrite(transport, setupOwnerOperation({ username, password, displayName: null }), SETUP_TEXT, signal);
}

export function exchangeLocalTokenWithTransport(
  transport: ApiTransportPort, token: string, signal: AbortSignal,
): Promise<SessionIdentity> {
  return runSetupWrite(transport, exchangeLocalTokenOperation({ token: token.trim() }), LOCAL_TOKEN_TEXT, signal);
}

async function runSetupWrite(
  transport: ApiTransportPort,
  operation: ReturnType<typeof setupOwnerOperation>,
  text: WriteText,
  signal: AbortSignal,
): Promise<SessionIdentity> {
  try {
    return await runOperation(transport, { ...operation, signal }, undefined);
  } catch (cause: unknown) {
    const result = readWriteFailure(cause, SETUP_FAILURES, text);
    if (result.is === 'done') throw new OwnerAlreadyConfiguredError();
    throw new Error(result.text, { cause });
  }
}
