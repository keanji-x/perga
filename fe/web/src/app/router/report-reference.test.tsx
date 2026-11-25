import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import type { ApiRequest } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { ReportReferencePreview } from './report-reference.tsx';
import { fixtureSnapshot, fixtureTrack } from './session-contract-fixture.ts';

afterEach(cleanup);

function preview(blockId: string | null, flow: unknown) {
  const track = fixtureTrack('flow-only');
  const send = vi.fn((request: ApiRequest) => Promise.resolve({
    status: 200,
    statusText: 'OK',
    body: request.path.endsWith('/views')
      ? { templateId: '', dataRev: 0, views: {}, widgets: {}, diagnostics: [],
        flowRev: 1, flow }
      : fixtureSnapshot(track),
  }));
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={client}>
    <ReportReferencePreview transport={{ send }} unauthorized={createUnauthorizedChannel({ enqueue: task => task() })}
      target={{ trackId: track.id, blockId }}
      onOpenLink={vi.fn()} onOpenSource={vi.fn()} onOpenFile={vi.fn()} />
  </QueryClientProvider>);
  return send;
}

it('renders flow in a whole-Report reference with no prose', async () => {
  preview(null, { nodes: [{ id: 'check-release', title: 'Check release' }] });
  expect((await screen.findAllByText('Check release')).length).toBeGreaterThan(0);
});

it('renders archived flow in a whole-Report reference with no prose or active nodes', async () => {
  preview(null, { archived: [{ id: 'check-release', title: 'Check release', archivedAtMs: 1 }] });
  expect(await screen.findByRole('button', { name: 'Archived (1)' })).toBeTruthy();
});

it('keeps a whole-Report reference empty when it has no prose or flow', async () => {
  const send = preview(null, { nodes: [], archived: [] });
  expect(await screen.findByText('This report has no content yet.')).toBeTruthy();
  await waitFor(() => expect(send).toHaveBeenCalledWith(expect.objectContaining({
    path: '/api/v1/tracks/flow-only/views',
  })));
});

it('keeps a missing block reference empty on a flow-only Report', async () => {
  const send = preview('missing', { nodes: [{ id: 'check-release', title: 'Check release' }] });
  expect(await screen.findByText('This report has no content yet.')).toBeTruthy();
  expect(send.mock.calls.some(([request]) => request.path.endsWith('/views'))).toBe(false);
});
