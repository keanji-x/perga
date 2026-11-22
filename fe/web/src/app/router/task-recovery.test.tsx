import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { taskDtoSchema } from '../../../../core/api/perga-v1.ts';
import type { ApiRequest, ApiTransportPort } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { deriveReportTasks } from '../../../../core/domain/report.ts';
import { TaskRecoveryDetails } from '../../features/report/task/recovery.tsx';
import { TaskRecovery, useCurrentTaskRows } from './task-recovery.tsx';
import { fixtureSnapshot, fixtureTrack } from './session-contract-fixture.ts';
import { queryKeys } from '../providers/query-keys.ts';

afterEach(cleanup);

function setup(status = 'failed', enableTaskActions = false) {
  const requests: ApiRequest[] = [];
  let task = taskDtoSchema.parse({ id: 'task', trackId: 'w1', blockId: 'block', taskKey: 'inspect', kind: 'agent',
    status, sessionId: 'worker', runCount: 2, createdAtMs: 1, updatedAtMs: 2, workerResult: { summary: 'Latest result' },
    gateVerdict: { passed: false, steps: [{ name: 'check', exitCode: 1, durationMs: 3, stdout: 'failed check', outputTail: '' }] } });
  const transport: ApiTransportPort = { send: request => {
    requests.push(request);
    return Promise.resolve({ status: 200, statusText: 'OK', body: request.method === 'GET'
      ? { ...fixtureSnapshot(fixtureTrack('w1')), tasks: [task] } : task });
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const unauthorized = createUnauthorizedChannel({ enqueue: task => task() });
  function Owner() {
    const rows = useCurrentTaskRows(transport, unauthorized, 'w1', deriveReportTasks([
      { id: 'block', kind: 'task', payload: { key: 'inspect', kind: 'codex', declared_by: 'user', ready: true, goal: 'Inspect' } },
    ]));
    return <><output aria-label="Inventory status">{rows[0]?.execution?.label}</output><TaskRecovery trackId="w1" taskKey="inspect" expanded
      transport={transport} unauthorized={unauthorized} openWorker={vi.fn()} openableWorkerIds={new Set(['worker'])} enableTaskActions={enableTaskActions} /></>;
  }
  render(<QueryClientProvider client={client}><Owner /></QueryClientProvider>);
  return { client, requests, advance: async (status: string) => {
    task = { ...task, status, runCount: task.runCount + 1, workerResult: 'New result', gateVerdict: null };
    await client.invalidateQueries({ queryKey: queryKeys.trackDetail('w1') });
  } };
}

it('shares the track snapshot between inventory and task details and refreshes latest evidence', async () => {
  const fixture = setup();
  expect(await screen.findByText('Run 2 · Failed')).toBeTruthy();
  expect(screen.getByText('failed check')).toBeTruthy();
  expect(screen.getByText(/Latest result/)).toBeTruthy();
  expect(fixture.requests.every(request => request.path === '/api/v1/tracks/w1')).toBe(true);
  await fixture.advance('running');
  expect(await screen.findByText('Run 3 · Running')).toBeTruthy();
  expect(screen.getByText('New result')).toBeTruthy();
  expect(screen.queryByText('failed check')).toBeNull();
  expect(screen.getByRole('status', { name: 'Inventory status' }).textContent).toBe('Running');
  expect(screen.queryByRole('button', { name: 'Cancel task' })).toBeNull();
});

it('cancels a running task when actions are enabled', async () => {
  const fixture = setup('running', true);
  await userEvent.click(await screen.findByRole('button', { name: 'Cancel task' }));
  await waitFor(() => expect(fixture.requests.some(request => request.path === '/api/v1/tracks/w1/tasks/inspect/cancel' && request.method === 'POST')).toBe(true));
});

it('keeps the refresh action busy while refreshing', async () => {
  const refresh = vi.fn();
  render(<TaskRecoveryDetails task={undefined} current={undefined} loading loadError={null}
    onRefresh={refresh} openWorker={undefined} openableWorkerIds={new Set()} />);
  const action = screen.getByRole('button', { name: 'Refresh execution history' });
  expect(action.getAttribute('aria-busy')).toBe('true');
  await userEvent.click(action);
  expect(refresh).not.toHaveBeenCalled();
});

it('groups a failed read with its recovery action', () => {
  render(<TaskRecoveryDetails task={undefined} current={undefined} loading={false} loadError="Task is unavailable."
    onRefresh={vi.fn()} openWorker={undefined} openableWorkerIds={new Set()} />);
  expect(screen.getByRole('alert').contains(screen.getByRole('button', { name: 'Refresh execution history' }))).toBe(true);
});
