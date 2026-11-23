import { useCallback, useEffect, useId, useLayoutEffect, useRef } from 'react';
import { useState } from '../../../ui/state/public.ts';
import styles from './login-page.module.css';
import type { SessionIdentity } from '../../../../../core/api/auth.ts';
import { LoginThrottledError } from '../../../../../core/domain/login.ts';
import { formatMessage, MESSAGES } from '../../../../../core/domain/messages.ts';

export type LoginPageProps = Readonly<{
  login: (username: string, password: string, signal: AbortSignal) => Promise<SessionIdentity | null>;
  reload: () => void;
}>;

function syncSystemTheme(): () => void {
  if (typeof window === 'undefined' || typeof document === 'undefined') return () => {};
  if (document.documentElement.dataset.theme === 'light' || document.documentElement.dataset.theme === 'dark') {
    return () => {};
  }
  if (typeof window.matchMedia !== 'function') return () => {};
  const media = window.matchMedia('(prefers-color-scheme: dark)');
  const apply = () => { document.documentElement.dataset.theme = media.matches ? 'dark' : 'light'; };
  apply();
  media.addEventListener('change', apply);
  return () => media.removeEventListener('change', apply);
}

export function LoginPage({ login, reload }: LoginPageProps) {
  const prefix = useId();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [retryAt, setRetryAt] = useState<number | null>(null);
  const [retrySeconds, setRetrySeconds] = useState(0);
  const pending = useRef<AbortController | null>(null);
  const cancel = useCallback(() => {
    const attempt = pending.current; pending.current = null; attempt?.abort();
  }, []);
  useLayoutEffect(() => cancel, [cancel]);
  useEffect(() => syncSystemTheme(), []);
  useEffect(() => {
    if (retryAt === null) return;
    const timer = window.setInterval(() => {
      const remaining = Math.max(0, Math.ceil((retryAt - Date.now()) / 1000));
      setRetrySeconds(remaining);
      if (remaining === 0) window.clearInterval(timer);
    }, 1000);
    return () => window.clearInterval(timer);
  }, [retryAt]);

  return <main className={styles.page}>
    <form className={styles.form} onSubmit={(event) => {
      event.preventDefault();
      if (pending.current !== null) return;
      if (username.trim() === '' || password.trim() === '') {
        setError(MESSAGES.auth.missingCredentials);
        return;
      }
      const attempt = new AbortController(); pending.current = attempt;
      setSubmitting(true); setError(null); setRetryAt(null); setRetrySeconds(0);
      void login(username, password, attempt.signal).then((result) => {
        if (attempt.signal.aborted) return;
        if (!result) { setError(MESSAGES.auth.invalidCredentials); setSubmitting(false); return; }
        // A reload resets every persisted and in-memory cache under the new identity.
        reload();
      }).catch((cause: unknown) => {
        if (attempt.signal.aborted) return;
        if (cause instanceof LoginThrottledError) {
          setRetryAt(Date.now() + cause.retryAfterSeconds * 1000);
          setRetrySeconds(cause.retryAfterSeconds);
        }
        setError(cause instanceof Error && cause.message ? cause.message : MESSAGES.auth.signInFailed);
        setSubmitting(false);
      }).finally(() => {
        if (pending.current === attempt) pending.current = null;
      });
    }}>
      <h1 className={styles.brand}>
        <span className={styles.mark} aria-hidden="true">✳</span>
        <span>Perga</span>
      </h1>

      {error !== null && <p role="alert" className={styles.error}>{error}</p>}
      {retryAt !== null && (
        <p className={styles.retryNote}>
          {retrySeconds > 0
            ? formatMessage(retrySeconds === 1 ? MESSAGES.auth.retryInOne : MESSAGES.auth.retryIn, { seconds: retrySeconds })
            : MESSAGES.auth.retryReady}
        </p>
      )}

      <div className={styles.field}>
        <label className={styles.label} htmlFor={`${prefix}-username`}>{MESSAGES.auth.usernameLabel}</label>
        <input
          id={`${prefix}-username`}
          className={styles.input}
          name="username"
          /* eslint-disable-next-line jsx-a11y/no-autofocus -- Single-purpose login screen intentionally starts at its first field. */
          autoFocus
          autoComplete="username"
          required
          value={username}
          onChange={(event) => setUsername(event.target.value)}
        />
      </div>

      <div className={styles.field}>
        <label className={styles.label} htmlFor={`${prefix}-password`}>{MESSAGES.auth.passwordLabel}</label>
        <input
          id={`${prefix}-password`}
          className={styles.input}
          name="password"
          type="password"
          autoComplete="current-password"
          required
          value={password}
          onChange={(event) => setPassword(event.target.value)}
        />
      </div>

      <button type="submit" className={styles.submit} disabled={submitting}>
        {submitting ? MESSAGES.auth.submittingLabel : MESSAGES.auth.submitLabel}
      </button>
    </form>
  </main>;
}
