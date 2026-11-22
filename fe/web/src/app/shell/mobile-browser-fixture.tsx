import { fixtureBootstrap, fixtureReport, fixtureSession, fixtureSnapshot, fixtureTrack } from '../router/session-contract-fixture.ts';
import { sessionPage } from '../router/conversation-session-data.ts';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider, createMemoryHistory, type RouterHistory } from '@tanstack/react-router';
import { render } from '@testing-library/react';
import type { ApiRequest, ApiTransportPort, ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { ThemeProvider } from '../theme/public.tsx';
import { createAppRouter } from '../router/public.tsx';
import type { ReadReceiptsKernel } from '../providers/read-receipts-fixture.tsx';
import { bootTestCardRuntime } from '../router/test-card-runtime.ts';
import { DepthFeedback } from '../../ui/motion/depth-feedback.tsx';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';

export const AREA = Object.freeze({ id: 'c1', name: 'Product', color: '#5B8DEF', sortOrder: 1, createdAtMs: 1, updatedAtMs: 1 });
export function setup(path: string, areaName: string = AREA.name, onRequest: (request: ApiRequest) => void = () => undefined, receipts?: ReadReceiptsKernel, history?: RouterHistory) {
  const OTHER_AREA = { ...AREA, id: 'c2', name: 'Frontend', color: '#8B7FE8', sortOrder: 2 };
  const TRACK = fixtureTrack('w1', { areaId: 'c1', title: 'Responsive mobile UI', pinnedAtMs: 30, leadSessionId: 'card-review' });
  const OTHER_TRACK = fixtureTrack('w2', { areaId: 'c1', title: 'Remote access', sortOrder: 2 });
  const report = fixtureReport('## Mobile workspace direction\n\nReport stays at the root. Navigation, cards and conversations arrive as focused pages instead of squeezing the document.\n\n## Why this shape\n\nThe phone gets one clear reading surface. Secondary work remains one gesture away and always has an explicit route back to Report.');
  report.blocks.splice(1, 0, { id: 'b-task-layout', kind: 'task', rev: 1, markdown: '', payload: { key: 'mobile-layout', kind: 'codex', ready: true, goal: 'Validate the right-push interaction on a 390 × 844 viewport.' } });
  report.blocks.push({ id: 'b-task-touch', kind: 'task', rev: 1, markdown: '', payload: { key: 'touch-targets', kind: 'codex', ready: false, goal: 'Every control clears 44px.' } });
  const session = fixtureSession('card-review', 'w1', { title: 'Design review', isLead: true });
  const areas = [{ ...AREA, name: areaName }, OTHER_AREA];
  const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });

  const transport: ApiTransportPort = {
    async send(request) {
      onRequest(request);
      const receipt = await receipts?.reply(request);
      if (receipt !== undefined) return receipt;
      if (request.path.includes('/workspace-options')) return ok({ defaultId: 'direct', options: [{ id: 'direct', label: 'Direct folder', description: 'Use this folder' }] });
      if (request.path.startsWith('/api/v1/bootstrap')) return ok(fixtureBootstrap());
      if (request.path === '/api/v1/areas' && request.method === 'POST') {
        const body = request.body as { name: string; color: string; defaultCwd?: string | null };
        const created = { ...AREA, id: `c${areas.length + 1}`, name: body.name, color: body.color, defaultCwd: body.defaultCwd ?? null, sortOrder: areas.length + 1 };
        areas.push(created);
        return { status: 201, statusText: 'Created', body: created };
      }
      if (request.path === '/api/v1/areas') return ok(areas);
      if (request.path === '/api/v1/areas/c1/tracks?includeClosed=true') return ok([TRACK, OTHER_TRACK]);
      if (request.path === '/api/v1/areas/c2/tracks?includeClosed=true') return ok([]);
      if (request.path === '/api/v1/tracks/w1') return ok({ ...fixtureSnapshot(TRACK, [session], report), tasks: [
        { id: 'layout', trackId: 'w1', blockId: 'b-task-layout', taskKey: 'mobile-layout',
          kind: 'agent', ready: true, status: 'pending', createdAtMs: 1, updatedAtMs: 1 },
        { id: 'touch', trackId: 'w1', blockId: 'b-task-touch', taskKey: 'touch-targets',
          kind: 'agent', ready: false, status: 'draft', createdAtMs: 1, updatedAtMs: 1 },
      ] });
      if (request.path === '/api/v1/tracks/w2') return ok(fixtureSnapshot(OTHER_TRACK));
      if (request.path === '/api/v1/tracks/w1/terminals') return ok([{ id: 'card-terminal', trackId: 'w1', title: 'Implementation terminal', cwd: '/tmp', status: 'exited', createdAtMs: 1 }]);
      if (request.path === '/api/v1/tracks/w1/sessions') return ok([session]);
      if (request.path.startsWith('/api/v1/sessions/card-review/items?')) return ok(sessionPage(session));
      return Promise.resolve(ok([]));
    },
  };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter({ prepareReport: prepareReportDocument,
    transport,
    unauthorized: createUnauthorizedChannel({ enqueue: (task) => task() }),
    client,
    cards: bootTestCardRuntime(),
  });
  router.update({ history: history ?? createMemoryHistory({ initialEntries: [path] }) });
  render(<QueryClientProvider client={client}><ThemeProvider storage={{ getItem: () => null, setItem: () => undefined }}>
    <DepthFeedback />
    <RouterProvider router={router} />
  </ThemeProvider></QueryClientProvider>);
  return router;
}
