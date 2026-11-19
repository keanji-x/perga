import { ServerCompatGate, WEB_COMPAT_VERSION } from './public.tsx';
import { QueryClient } from '@tanstack/react-query';
import { DB_INSTANCE_ID_KEY, DATABASE_ID_KEY } from '../../../../core/keys/storage.ts';
import { SystemUpgradeProvider } from './connection-status.tsx';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import { SystemUpgradeMonitor, useSystemUpgrade } from './system-upgrade.tsx';
import { ConnectionStatusScope } from './connection-status.tsx';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';

afterEach(() => { cleanup(); vi.useRealTimers(); });
function Observation() {
  const state = useSystemUpgrade();
  return <output>{state?.info?.webBuildId}:{String(state?.webRefreshAvailable)}</output>;
}
it('reads on startup, polls, refreshes immediately on reconnect, and cleans up', async () => {
  vi.useFakeTimers();
  let build = 'web-1';
  const send = vi.fn(() => Promise.resolve({ status: 200, statusText: 'OK', body: { releaseId: 'r1', webBuildId: build, hostBuildId: 'h1', bridgeBuildId: 'b1', schemaVersion: 11, draining: false, activeDispatches: 0 } }));
  const transport = { send };
  const unauthorized = createUnauthorizedChannel({ enqueue: task => task() }, { report: vi.fn() });
  function Connection({ connected, children }: { connected: boolean; children: ReactNode }) {
    return <ConnectionStatusScope value={{ connected, label: '', detail: '' }}>{children}</ConnectionStatusScope>;
  }
  const content = (connected: boolean) => <SystemUpgradeProvider><Connection connected={connected}>
    <SystemUpgradeMonitor transport={transport} unauthorized={unauthorized} /><Observation />
  </Connection></SystemUpgradeProvider>;
  const mounted = render(content(false));
  await act(async () => { await vi.advanceTimersByTimeAsync(0); });
  expect(screen.getByRole('status').textContent).toBe('web-1:false');
  const startupReads = send.mock.calls.length;
  build = 'web-2';
  mounted.rerender(content(true));
  await act(async () => { await vi.advanceTimersByTimeAsync(0); });
  expect(send.mock.calls.length).toBe(startupReads + 1);
  expect(screen.getByRole('status').textContent).toBe('web-2:true');
  await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
  expect(send.mock.calls.length).toBe(startupReads + 2);
  mounted.unmount();
  await act(async () => { await vi.advanceTimersByTimeAsync(10000); });
  expect(send.mock.calls.length).toBe(startupReads + 2);
});

it.each([['stable-db', false], ['different-db', true]] as const)('handles a Perga Host boot with database %s (reload: %s)', async (databaseId, switched) => {
  const values = new Map<string, string>([[DB_INSTANCE_ID_KEY, 'old-boot'], [DATABASE_ID_KEY, 'stable-db']]);
  const reload = vi.fn();
  const client = new QueryClient();
  const runtime = { fetchVersion: () => Promise.resolve({ pergaV1: true, webCompatVersion: WEB_COMPAT_VERSION, minWebCompatVersion: WEB_COMPAT_VERSION, syncEventVersion: 1, dbInstanceId: 'new-boot', databaseId }),
    reload, deleteDatabase: vi.fn(), idbDatabaseName: 'test-db', storage: { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); }, removeItem: (key: string) => { values.delete(key); } } };
  render(<ServerCompatGate client={client} runtime={runtime} cursorStore={{ clear: vi.fn() }} renderEventBridge={() => <span>Live bridge</span>}><textarea aria-label="Draft" defaultValue="keep me" /></ServerCompatGate>);
  if (switched) {
    await waitFor(() => expect(reload).toHaveBeenCalledOnce());
    expect(screen.queryByRole('textbox')).toBeNull();
  } else {
    await waitFor(() => expect(screen.getByText('Live bridge')).toBeTruthy());
    expect(reload).not.toHaveBeenCalled();
    expect(screen.getByRole<HTMLTextAreaElement>('textbox').value).toBe('keep me');
  }
});
