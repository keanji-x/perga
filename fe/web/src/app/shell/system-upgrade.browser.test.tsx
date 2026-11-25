import '../../styles/entry.css';
import { SystemUpgradeProvider } from '../providers/connection-status.tsx';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { SystemInfoDto } from '../../../../core/api/perga-v1.ts';
import { EventStream, type EventStreamSink } from '../../systems/events/event-stream.ts';
import { EventBridge } from '../events/event-bridge.tsx';
import { LiveConnectionProvider } from '../providers/connection-status.tsx';
import { SystemUpgradeMonitor, useSystemUpgrade } from '../providers/system-upgrade.tsx';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { SystemUpgradeBannerHost } from './system-upgrade-banner.tsx';
import { AboutPane } from '../../features/settings/public.tsx';

afterEach(cleanup);
function VersionCard() { return <AboutPane system={useSystemUpgrade()} />; }
it('keeps draft text through draining, restart, and build changes and displays the version card', async () => {
  await page.viewport(1100, 800);
  let info: SystemInfoDto = { releaseId: 'r1', webBuildId: 'w1', hostBuildId: 'h1', bridgeBuildId: 'b1', schemaVersion: 11, draining: false, activeDispatches: 0 };
  const send = vi.fn(() => Promise.resolve({ status: 200, statusText: 'OK', body: info }));
  const transport = { send };
  const unauthorized = createUnauthorizedChannel({ enqueue: callback => callback() }, { report: vi.fn() });
  let sink!: EventStreamSink;
  const stream = EventStream.create('ws://test.invalid', { start: (_configuration, _url, next) => { sink = next; next.connectionState('connected'); }, stop: vi.fn() });
  const client = new QueryClient();
  const cursor = { read: () => null, write: vi.fn(), adopt: vi.fn(), clear: vi.fn() };
  render(<QueryClientProvider client={client}><SystemUpgradeProvider><LiveConnectionProvider>
    <EventBridge client={client} stream={stream} cursor={cursor} syncEventVersion={1} dbInstanceId="test" />
    <SystemUpgradeMonitor transport={transport} unauthorized={unauthorized} />
    <SystemUpgradeBannerHost /><textarea aria-label="Unsent message" /><VersionCard />
  </LiveConnectionProvider></SystemUpgradeProvider></QueryClientProvider>);
  await waitFor(() => expect(screen.getByText('w1')).toBeTruthy());
  const details = screen.getByText('Diagnostics').closest('details')!;
  expect(details.open).toBe(false);
  await userEvent.click(screen.getByText('Diagnostics'));
  expect(details.open).toBe(true);
  expect(screen.getByText('w1').getBoundingClientRect().height).toBeGreaterThan(0);
  await userEvent.fill(screen.getByRole('textbox'), 'Keep my unsent draft');
  act(() => sink.frame({ type: 'event', meta: { id: 0, eventVersion: 1 }, event: { ev: 'system.upgrade_status', data: { draining: true, activeDispatches: 2 } } }));
  expect(screen.getByRole('status').textContent).toContain('2 active dispatches');
  expect(screen.getByRole('complementary', { name: 'System upgrade' }).getBoundingClientRect().top).toBeLessThan(screen.getByRole('textbox').getBoundingClientRect().top);
  info = { ...info, releaseId: 'r2', draining: true, activeDispatches: 2 };
  act(() => sink.connectionState('disconnected'));
  expect(screen.getByRole('status').textContent).toContain('Reconnecting during upgrade');
  act(() => sink.connectionState('connected'));
  await waitFor(() => expect(screen.getAllByText('r2').length).toBeGreaterThanOrEqual(1));
  expect(screen.queryByRole('button', { name: 'Refresh' })).toBeNull();
  info = { ...info, webBuildId: 'w2', draining: false, activeDispatches: 0 };
  const reads = send.mock.calls.length;
  act(() => sink.connectionState('disconnected'));
  act(() => sink.connectionState('connected'));
  // Separate transitions can coalesce, so the periodic read is also exercised.
  await waitFor(() => expect(send.mock.calls.length).toBeGreaterThan(reads), { timeout: 7000 });
  await waitFor(() => expect(screen.getAllByRole('button', { name: 'Refresh' })).toHaveLength(1));
  expect(screen.getByRole<HTMLTextAreaElement>('textbox').value).toBe('Keep my unsent draft');
  expect(screen.getByText('w2')).toBeTruthy();
  act(() => sink.frame({ type: 'event', meta: { id: 0, eventVersion: 1 }, event: { ev: 'system.upgrade_status', data: { draining: false, activeDispatches: 0, receipt: { jobId: 'j1', oldReleaseId: 'r1', newReleaseId: 'r2', status: 'completed', error: null, rollbackError: null, notifyTrackId: null, finishedAt: 100, changed: ['web'] } } } }));
  expect(screen.getByText('Latest upgrade receipt')).toBeTruthy();
  await userEvent.click(screen.getByRole('button', { name: 'Dismiss upgrade receipt' }));
  expect(screen.getAllByText(/Upgrade completed/)).toHaveLength(1); // Settings retains the receipt.
  expect(screen.getAllByRole('button', { name: 'Refresh' })).toHaveLength(1);
});
