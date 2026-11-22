import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, render, screen } from '@testing-library/react';
import { page, userEvent } from 'vitest/browser';
import { afterEach, expect, it } from 'vitest';
import { taskDtoSchema } from '../../../../core/api/perga-v1.ts';
import type { ApiRequest, ApiTransportPort, ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { deriveReportTasks, type TrackReport } from '../../../../core/domain/report.ts';
import { ReportDocument } from '../../features/report/document/public.tsx';
import '../../styles/entry.css';
import { TaskRecovery, useCurrentTaskRows } from './task-recovery.tsx';
import { fixtureSnapshot, fixtureTrack } from './session-contract-fixture.ts';
import { queryKeys } from '../providers/query-keys.ts';

afterEach(cleanup);

it('shows latest task evidence and its session on desktop and phone and refreshes folded inventory', async () => {
  const requests: ApiRequest[] = [];
  let task = taskDtoSchema.parse({ id: 'task', trackId: 'w1', blockId: 'b-b', taskKey: 'b', kind: 'agent',
    status: 'failed', sessionId: 'worker', runCount: 2, statusDetail: 'Validation failed', createdAtMs: 1, updatedAtMs: 2,
    workerResult: { summary: 'Latest worker result' }, gateVerdict: { passed: false,
      steps: [{ name: 'check', exitCode: 1, durationMs: 10, stdout: 'Check failed', outputTail: '' }] } });
  let conversation: string | null = null;
  const transport: ApiTransportPort = { send: request => {
    requests.push(request);
    return Promise.resolve({ status: 200, statusText: 'OK', body: { ...fixtureSnapshot(fixtureTrack('w1')), tasks: [task] } });
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const unauthorized = createUnauthorizedChannel({ enqueue: task => task() });
  const report: TrackReport = { summary: '', body: '', blocks: [{ id: 'b-b', kind: 'task', payload: {
    key: 'b', kind: 'codex', declared_by: 'user', ready: true, goal: 'Finish the calculation under the original requirements.',
  } }] };
  function Document() {
    const rows = useCurrentTaskRows(transport, unauthorized, 'w1', deriveReportTasks(report.blocks));
    return <ReportDocument report={report} taskRows={rows} empty={null}
      renderTaskExecution={(row, expanded) => <TaskRecovery trackId="w1" taskKey={row.key} expanded={expanded}
        transport={transport} unauthorized={unauthorized} openableWorkerIds={new Set(['worker'])}
        openWorker={id => { conversation = id; }} />} />;
  }
  await page.viewport(1080, 800);
  render(<QueryClientProvider client={client}><div style={{ padding: 24 }}><Document /></div></QueryClientProvider>);
  await userEvent.click(document.querySelector('[data-nc-report-reference] > summary')!);
  await userEvent.click(document.querySelector('[data-nc-task-state] > summary')!);
  await expect.element(page.getByText('Run 2 · Failed')).toBeVisible();
  await expect.element(page.getByText('Check failed', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Open task session' }).click();
  expect(conversation).toBe('worker');
  expect(requests.every(request => request.method === 'GET' && request.path === '/api/v1/tracks/w1')).toBe(true);
  await page.viewport(390, 844);
  await expect.element(page.getByRole('button', { name: 'Refresh execution history' })).toBeVisible();
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(390);
  const disclosure = document.querySelector<HTMLDetailsElement>('[data-nc-task-state]')!;
  await userEvent.click(disclosure.querySelector('summary')!);
  task = { ...task, status: 'done', workerResult: 'Finished', gateVerdict: null };
  await act(() => client.invalidateQueries({ queryKey: queryKeys.trackDetail('w1') }));
  await expect.poll(() => disclosure.querySelector('summary')!.textContent).toContain('Completed');
  expect(disclosure.open).toBe(false);
});

it('keeps a failed task read recovery action focused during retry', async () => {
  let reads = 0;
  let finishRetry!: (response: ApiTransportResponse) => void;
  const retry = new Promise<ApiTransportResponse>(resolve => { finishRetry = resolve; });
  const transport: ApiTransportPort = { send() {
    return ++reads === 1 ? Promise.resolve({ status: 500, statusText: 'Internal Server Error', body: { error: 'Private details' } }) : retry;
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const unauthorized = createUnauthorizedChannel({ enqueue: task => task() });
  render(<QueryClientProvider client={client}><TaskRecovery trackId="w1" taskKey="b" expanded
    transport={transport} unauthorized={unauthorized} openableWorkerIds={new Set()} openWorker={() => {}} /></QueryClientProvider>);
  await expect.element(page.getByText('Could not refresh execution history.', { exact: true })).toBeVisible();
  expect(page.getByText('Private details').query()).toBeNull();
  const action = screen.getByRole('button', { name: 'Refresh execution history' });
  await userEvent.click(action);
  await expect.poll(() => action.getAttribute('aria-busy')).toBe('true');
  expect(document.activeElement).toBe(action);
  await userEvent.click(action, { force: true });
  expect(reads).toBe(2);
  await act(async () => { finishRetry({ status: 200, statusText: 'OK', body: fixtureSnapshot(fixtureTrack('w1')) }); await retry; });
  await expect.element(page.getByText('No execution yet', { exact: true })).toBeVisible();
  client.clear();
});
