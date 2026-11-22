import { loginOperation, type SessionIdentity } from '../../../../core/api/auth.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import { ApiError, classifyFailure, refusedText } from '../../../../core/domain/failure-class.ts';
import { LOGIN_FAILURES, LOGIN_TEXT, LoginThrottledError } from '../../../../core/domain/login.ts';
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
