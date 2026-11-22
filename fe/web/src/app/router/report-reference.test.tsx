import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import type { ApiRequest } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { ReportReferencePreview } from './report-reference.tsx';
import { fixtureSnapshot, fixtureTrack } from './session-contract-fixture.ts';

afterEach(cleanup);

function preview(blockId: string | null) {
  const track = fixtureTrack('flow-only');
  const send = vi.fn((request: ApiRequest) => Promise.resolve({
    status: 200,
    statusText: 'OK',
    body: request.path.endsWith('/views')
      ? { templateId: '', dataRev: 0, views: {}, widgets: {}, diagnostics: [], humanReadOnly: true,
        flowRev: 1, flow: { rev: 1, nodes: [{ id: 'check-release', title: 'Check release' }] } }
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
  preview(null);
  expect((await screen.findAllByText('Check release')).length).toBeGreaterThan(0);
});

it('keeps a missing block reference empty on a flow-only Report', async () => {
  const send = preview('missing');
  expect(await screen.findByText('这份报告暂无内容。')).toBeTruthy();
  expect(send.mock.calls.some(([request]) => request.path.endsWith('/views'))).toBe(false);
});
