import { useId, useLayoutEffect, useRef, type FormEvent, type ReactNode } from 'react';
import { useState } from '../../../ui/state/public.ts';
import { OwnerAlreadyConfiguredError } from '../../../../../core/domain/login.ts';
import styles from './login-page.module.css';

export function AuthSetupPage({ title, children }: Readonly<{ title: string; children: ReactNode }>) {
  return <main className={styles.page}>
    <div className={styles.form}>
      <div className={styles.brand}><span className={styles.mark} aria-hidden="true" />Perga</div>
      <h1 className={styles.setupTitle}>{title}</h1>
      {children}
    </div>
  </main>;
}

function SetupForm({ children, submitLabel, submit, onComplete, onConfigured }: Readonly<{
  children: ReactNode;
  submitLabel: string;
  submit: (signal: AbortSignal) => Promise<unknown>;
  onComplete: () => void;
  onConfigured?: () => void;
}>) {
  const pending = useRef<AbortController | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useLayoutEffect(() => () => pending.current?.abort(), []);

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (pending.current !== null) return;
    const attempt = new AbortController();
    pending.current = attempt;
    setSubmitting(true);
    setError(null);
    void submit(attempt.signal).then(() => {
      if (!attempt.signal.aborted) onComplete();
    }).catch((cause: unknown) => {
      if (attempt.signal.aborted) return;
      if (cause instanceof OwnerAlreadyConfiguredError && onConfigured !== undefined) {
        onConfigured();
        return;
      }
      setError(cause instanceof Error ? cause.message : 'Could not complete the request.');
      setSubmitting(false);
    }).finally(() => {
      if (pending.current === attempt) pending.current = null;
    });
  }

  return <form className={styles.setupForm} onSubmit={handleSubmit}>
    {error !== null && <p className={styles.error} role="alert">{error}</p>}
    {children}
    <button className={styles.submit} type="submit" disabled={submitting}>
      {submitting ? 'Please wait…' : submitLabel}
    </button>
  </form>;
}

export function OwnerSetupForm({ submitLabel, setup, onComplete, onConfigured }: Readonly<{
  submitLabel: string;
  setup: (username: string, password: string, signal: AbortSignal) => Promise<unknown>;
  onComplete: () => void;
  onConfigured: () => void;
}>) {
  const id = useId();
  const [username, setUsername] = useState('owner');
  const [password, setPassword] = useState('');
  const [visible, setVisible] = useState(false);
  return <SetupForm submitLabel={submitLabel} submit={(signal) => setup(username, password, signal)}
    onComplete={onComplete} onConfigured={onConfigured}>
    <div className={styles.field}>
      <label className={styles.label} htmlFor={`${id}-username`}>Username</label>
      <input id={`${id}-username`} className={styles.input} name="username" autoComplete="username" required
        value={username} onChange={(event) => setUsername(event.target.value)} />
    </div>
    <div className={styles.field}>
      <label className={styles.label} htmlFor={`${id}-password`}>Password</label>
      <div className={styles.passwordRow}>
        <input id={`${id}-password`} className={styles.input} name="password" type={visible ? 'text' : 'password'}
          autoComplete="new-password" aria-describedby={`${id}-hint`} required
          value={password} onChange={(event) => setPassword(event.target.value)} />
        <button type="button" className={styles.secondary} aria-controls={`${id}-password`}
          onClick={() => setVisible(!visible)}>{visible ? 'Hide password' : 'Show password'}</button>
      </div>
      <p className={styles.retryNote} id={`${id}-hint`}>At least 12 characters</p>
    </div>
  </SetupForm>;
}

export function LocalTokenForm({ exchange, onComplete, onConfigured }: Readonly<{
  exchange: (token: string, signal: AbortSignal) => Promise<unknown>;
  onComplete: () => void;
  onConfigured: () => void;
}>) {
  const id = useId();
  const [token, setToken] = useState('');
  return <SetupForm submitLabel="Open workspace" submit={(signal) => exchange(token, signal)}
    onComplete={onComplete} onConfigured={onConfigured}>
    <div className={styles.field}>
      <label className={styles.label} htmlFor={id}>Local launch token</label>
      <input id={id} className={styles.input} name="token" type="password" autoComplete="off" required
        value={token} onChange={(event) => setToken(event.target.value)} />
    </div>
  </SetupForm>;
}

export function AuthSetupRetry({ onRetry }: Readonly<{ onRetry: () => void }>) {
  return <button type="button" className={styles.secondary} onClick={onRetry}>Check again</button>;
}
