import { useRef, type ReactNode } from 'react';
import { logoutOperation } from '../../../../core/api/auth.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import { useState } from '../../ui/state/public.ts';
import { runOperation } from '../providers/query-operation.ts';
import { SignOutScope } from './current-user.tsx';
import styles from './logout-gate.module.css';

type LogoutState = Readonly<{ sessionId: string; status: 'pending' | 'failed' }>;

export function LogoutGate({ transport, onComplete, children }: Readonly<{
  transport: ApiTransportPort; onComplete: () => void; children: ReactNode;
}>) {
  const [state, setState] = useState<LogoutState | null>(null);
  const pending = useRef(false);

  const signOut = (sessionId: string) => {
    if (pending.current) return;
    pending.current = true;
    setState({ sessionId, status: 'pending' });
    void runOperation(transport, logoutOperation(sessionId), undefined).then(onComplete, () => {
      pending.current = false;
      setState({ sessionId, status: 'failed' });
    });
  };

  if (state === null) return <SignOutScope value={signOut}>{children}</SignOutScope>;
  return <main className={styles.page}>
    <section className={styles.panel} aria-label="Sign out">
      {state.status === 'pending'
        ? <p role="status">Signing out…</p>
        : <>
          <p role="alert">Could not confirm sign-out.</p>
          <button type="button" onClick={() => signOut(state.sessionId)}>Retry sign out</button>
        </>}
    </section>
  </main>;
}
