import { useEffect } from 'react';
import type { AuthSetupStatus } from '../../../../core/api/auth.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import { LoginPage } from '../../features/auth/login-page/public.tsx';
import { AuthSetupPage, AuthSetupRetry, LocalTokenForm, OwnerSetupForm } from '../../features/auth/login-page/setup.tsx';
import { LoadingStatus } from '../../ui/loading-status/public.tsx';
import { useState } from '../../ui/state/public.ts';
import { exchangeLocalTokenWithTransport, fetchAuthSetupStatus, loginWithTransport, setupOwnerWithTransport } from './login.ts';

export function AuthGate({ transport, reload }: Readonly<{ transport: ApiTransportPort; reload: () => void }>) {
  const [result, setResult] = useState<AuthSetupStatus | 'failed' | null>(null);
  const [revision, setRevision] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    void fetchAuthSetupStatus(transport, controller.signal).then((status) => {
      if (!controller.signal.aborted) setResult(status);
    }).catch(() => {
      if (!controller.signal.aborted) setResult('failed');
    });
    return () => controller.abort();
  }, [transport, revision]);

  const checkAgain = () => { setResult(null); setRevision((previous) => previous + 1); };
  const showLogin = () => setResult({
    mode: 'configured', needsOwnerSetup: false, canSetupInBrowser: false, setupBlockedReason: null, nextStep: null,
  });

  if (result === null)
    return <AuthSetupPage title="Opening Perga"><LoadingStatus>Checking account setup…</LoadingStatus></AuthSetupPage>;
  if (result === 'failed')
    return <AuthSetupPage title="Account setup">
      <p role="alert">Could not check account setup.</p>
      <AuthSetupRetry onRetry={checkAgain} />
    </AuthSetupPage>;
  if (result.mode === 'configured')
    return <LoginPage login={(username, password, signal) => loginWithTransport(transport, username, password, signal)}
      reload={reload} />;
  if (result.mode === 'local_token_required')
    return <AuthSetupPage title="Open your local workspace">
      <p>{result.nextStep ?? 'Open the launch link from your terminal or paste your local launch token.'}</p>
      <LocalTokenForm exchange={(token, signal) => exchangeLocalTokenWithTransport(transport, token, signal)}
        onComplete={reload} onConfigured={showLogin} />
    </AuthSetupPage>;
  if (!result.canSetupInBrowser)
    return <AuthSetupPage title="Set up Perga on the host">
      <p>{result.setupBlockedReason === 'existing_data'
        ? 'This workspace already contains data. Create its owner on the host to keep it.'
        : 'Account setup requires a direct local connection to the host.'}</p>
      {result.nextStep !== null && <p>{result.nextStep}</p>}
      <AuthSetupRetry onRetry={checkAgain} />
    </AuthSetupPage>;
  return <AuthSetupPage title="Create your account">
    <OwnerSetupForm submitLabel="Create account" setup={(username, password, signal) => setupOwnerWithTransport(transport, username, password, signal)}
      onComplete={reload} onConfigured={showLogin} />
  </AuthSetupPage>;
}
