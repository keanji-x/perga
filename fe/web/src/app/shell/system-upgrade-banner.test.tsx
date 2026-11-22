import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { SystemUpgradeStore } from '../../../../core/domain/system-upgrade.ts';
import { SystemUpgradeBanner } from './system-upgrade-banner.tsx';
import { AboutPane } from '../../features/settings/public.tsx';

afterEach(cleanup);
it('shows draining and upgrade reconnect status without a refresh for a release-only change', () => {
  const store = new SystemUpgradeStore();
  store.acceptInfo({ releaseId: 'r1', webBuildId: 'w1', hostBuildId: 'h1', bridgeBuildId: 'b1', schemaVersion: 11, draining: true, activeDispatches: 3 });
  const refresh = vi.fn();
  const mounted = render(<SystemUpgradeBanner state={store.read()} connected onRefresh={refresh} onDismiss={vi.fn()} />);
  expect(screen.getByRole('status').textContent).toContain('3 active dispatches');
  mounted.rerender(<SystemUpgradeBanner state={store.read()} connected={false} onRefresh={refresh} onDismiss={vi.fn()} />);
  expect(screen.getByRole('status').textContent).toContain('Reconnecting during upgrade');
  store.acceptInfo({ ...store.read().info!, releaseId: 'r2' });
  mounted.rerender(<SystemUpgradeBanner state={store.read()} connected onRefresh={refresh} onDismiss={vi.fn()} />);
  expect(screen.queryByRole('button', { name: 'Refresh' })).toBeNull();
  expect(refresh).not.toHaveBeenCalled();
});
it('offers one explicit refresh, preserves a mounted draft, and dismisses only the receipt', () => {
  const store = new SystemUpgradeStore();
  store.acceptInfo({ releaseId: 'r1', webBuildId: 'w1', hostBuildId: 'h1', bridgeBuildId: 'b1', schemaVersion: 11, draining: false, activeDispatches: 0 });
  const refresh = vi.fn();
  function content() { return <><SystemUpgradeBanner state={store.read()} connected onRefresh={refresh} onDismiss={() => store.dismissReceipt()} /><textarea aria-label="Draft" defaultValue="unsent words" /></>; }
  const mounted = render(content());
  store.acceptInfo({ ...store.read().info!, webBuildId: 'w2' });
  store.acceptStatus({ draining: false, activeDispatches: 0, receipt: { jobId: 'j1', oldReleaseId: 'r1', newReleaseId: 'r2', status: 'rolled-back', error: 'Failed health check', rollbackError: null, notifyTrackId: null, finishedAt: 100, changed: ['host'] } });
  mounted.rerender(content());
  expect(screen.getAllByRole('button', { name: 'Refresh' })).toHaveLength(1);
  expect(screen.getByRole<HTMLTextAreaElement>('textbox').value).toBe('unsent words');
  expect(refresh).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Dismiss upgrade receipt' }));
  mounted.rerender(content());
  expect(screen.queryByText(/rolled-back/)).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
  expect(refresh).toHaveBeenCalledTimes(1);
});
it('shows current system identity, drain counts, and latest receipt in About', () => {
  const store = new SystemUpgradeStore();
  act(() => store.acceptInfo({ releaseId: 'release-current', webBuildId: 'web-current', hostBuildId: 'host-resident', bridgeBuildId: 'bridge', schemaVersion: 11, draining: true, activeDispatches: 4 }));
  store.acceptStatus({ draining: true, activeDispatches: 4, receipt: { jobId: 'latest-job', oldReleaseId: 'old-release', newReleaseId: 'release-current', status: 'completed', error: null, rollbackError: null, notifyTrackId: null, finishedAt: 100, changed: ['web'] } });
  render(<AboutPane system={store.read()} />);
  expect(screen.getByText('Latest upgrade receipt')).toBeTruthy();
  expect(screen.getByText(/Upgrade completed: old-release/)).toBeTruthy();
  for (const value of ['Release', 'release-current', 'Web build', 'web-current', 'Host build', 'host-resident', 'Schema version', '11', 'Draining', 'true', 'Active dispatches', '4']) expect(screen.getByText(value)).toBeTruthy();
});
