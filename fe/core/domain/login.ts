import type { FailureTable, WriteText } from './failure-class.js';

/**
 * What a failed `POST /api/v1/auth/login` means. `credentials`: the 401 the server answers for a wrong username or
 * password, which the form words itself. `refused`: another answered 4xx, shown with its reason; among them the 429
 * `login_throttled` a peer gets after repeated failures, whose reason says when to try again. `unknown`: no
 * trustworthy answer (lost, a 5xx, unreadable), shown as `LOGIN_TEXT.unknown`. A retry is safe: each success only
 * mints a new session.
 */
export type LoginFailure = 'credentials' | 'refused' | 'unknown';

export const LOGIN_FAILURES: FailureTable<LoginFailure> = Object.freeze({
  rules: Object.freeze([
    Object.freeze({ status: Object.freeze({ from: 400, to: 499 }), is: 'refused' as const }),
  ]),
  unauthorized: 'credentials',
  otherwise: 'unknown',
});

/** Neither sentence speaks of the connection: that is the global indicator's. */
export const LOGIN_TEXT: WriteText = Object.freeze({
  refused: 'Sign-in was refused.',
  unknown: 'Sign-in could not be completed. Try again.',
});

export class LoginThrottledError extends Error {
  readonly retryAfterSeconds: number;

  constructor(retryAfterSeconds: number) {
    super('Too many login attempts.');
    this.retryAfterSeconds = retryAfterSeconds;
  }
}

type AuthWriteFailure = 'refused' | 'unknown';

export const SETUP_FAILURES: FailureTable<AuthWriteFailure | 'done'> = Object.freeze({
  rules: Object.freeze([
    Object.freeze({ status: Object.freeze([409]), code: 'owner_already_configured', is: 'done' as const }),
    Object.freeze({ status: Object.freeze({ from: 400, to: 499 }), is: 'refused' as const }),
  ]),
  unauthorized: 'refused',
  otherwise: 'unknown',
});

export const SETUP_TEXT: WriteText = Object.freeze({
  refused: 'Account setup was refused.',
  unknown: 'Account setup could not be confirmed. Try again.',
});

export class OwnerAlreadyConfiguredError extends Error {
  constructor() { super('An owner account already exists.'); }
}

export const LOCAL_TOKEN_TEXT: WriteText = Object.freeze({
  refused: 'The local launch token was refused.',
  unknown: 'The local session could not be opened. Try again.',
});
