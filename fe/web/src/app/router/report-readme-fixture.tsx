import { sessionPage, sessionItem } from './conversation-session-data.ts';
import { fixtureSnapshot, fixtureSession, fixtureTrack, fixtureReport } from './session-contract-fixture.ts';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
// Scripted v1 responses exercise the real router and document renderer.
// The long document includes beginning and ending links for keyboard and wheel checks.
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { render } from '@testing-library/react';
import type { ApiRequest, ApiTransportPort, ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { ThemeProvider } from '../theme/public.tsx';
import { createAppRouter } from './public.tsx';
import { bootTestCardRuntime } from './test-card-runtime.ts';
import { withReadReceipts } from '../providers/read-receipts-fixture.tsx';

const readme = `# Documentation

Read the [English README](../README.md) for the high-level overview.

## Use and operate Perga

| Guide | Summary |
| --- | --- |
| [Using Perga](./using-perga.md) | Day-to-day workspace workflow |

## Develop and understand the system

| Reference | Summary |
| --- | --- |
| Architecture | Core boundaries and verification |
${Array.from({ length: 18 }, (_, index) => `## Documentation topic ${index}\n\n${'Detailed guidance for reading and operating the workspace. '.repeat(12)}`).join('\n\n')}

[Prose ratchet](./prose-ratchet.md)
`;

const rootReadme = `# Perga

## Why Perga?

Durable tracks, structured reports, and live agent sessions in one workspace.
`;

export const README_TRACK_ID = 'aadd6ed448d349228d672b1546b6a48c';
export const README_TRACK_URL = `/track/${README_TRACK_ID}`;
export const README_FILE_URL = `${README_TRACK_URL}?file=docs%2FREADME.md`;

export function renderReadmeFixture(initial: string, detailReady: Promise<void> = Promise.resolve(),
  options: Readonly<{ shortReport?: boolean; failFileRead?: boolean; fileReadReady?: () => Promise<void> }> = {}) {
  let fileReadFailed = options.failFileRead ?? false;
  const requests: ApiRequest[] = [];
  const track = fixtureTrack(README_TRACK_ID, { areaId: 'project', title: 'Documentation preview', cwd: '/repo', sortOrder: 1, closedAtMs: null, createdAtMs: 1, updatedAtMs: 2 });
  const card = fixtureSession('planner', track.id, { title: 'Existing conversation', isLead: true });
  const report = fixtureReport(`# Report\n\n${options.shortReport ? '' : readme}\n\nRead [docs/README.md](docs/README.md).`);
  const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
  const transport: ApiTransportPort = { async send(request) {
    requests.push(request);
    if (request.method !== 'GET') throw new Error(`Unexpected write: ${request.method} ${request.path}`);
    if (request.path === '/api/v1/areas') return ok([{ id: 'project', name: 'Project', color: '#6574cd', sortOrder: 1, kind: 'user', createdAtMs: 1, updatedAtMs: 1 }]);
    if (request.path === '/api/v1/areas/project/tracks?includeClosed=true') return ok([track]);
    if (request.path === `/api/v1/tracks/${track.id}`) {
      await detailReady;
      return ok(fixtureSnapshot(track, [card], report));
    }
    if (request.path === `/api/v1/tracks/${track.id}/report`) return ok({ taskDiagnostics: [] });
    if (request.path === `/api/v1/tracks/${track.id}/files?path=docs%2FREADME.md`) {
      await options.fileReadReady?.();
      if (fileReadFailed) return { status: 500, statusText: 'Internal Server Error',
        body: { code: 'internal', message: 'secure workspace reads require Linux openat2 support' } };
      return ok({ path: 'docs/README.md', size: new TextEncoder().encode(readme).length, text: readme, truncated: false });
    }
    if (request.path === `/api/v1/tracks/${track.id}/files?path=docs%2F..%2FREADME.md`) {
      return ok({ path: 'README.md', size: new TextEncoder().encode(rootReadme).length, text: rootReadme, truncated: false });
    }
    if (request.path.endsWith('/files?path=docs%2Fprose-ratchet.md')) return ok({ path: 'docs/prose-ratchet.md', size: 46, text: '# Prose ratchet\n\n[Documentation](./README.md)\n', truncated: false });
    if (request.path.includes('/files?path=')) throw new Error(`Unexpected file read: ${request.path}`);
    if (request.path.includes('/items?')) return ok(sessionPage(card, [sessionItem(card.id, 1, 'Conversation remains available while reading documentation.')]));
    if (request.path === '/api/settings') return ok({});
    return ok([]);
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter({ prepareReport: prepareReportDocument, transport: withReadReceipts(transport), client, cards: bootTestCardRuntime(),
    unauthorized: createUnauthorizedChannel({ enqueue: task => task() }) });
  router.update({ history: createMemoryHistory({ initialEntries: [initial] }) });
  const container = document.createElement('div');
  container.id = 'root';
  document.body.append(container);
  render(<QueryClientProvider client={client}><ThemeProvider storage={{ getItem: () => null, setItem: () => undefined }}>
    <RouterProvider router={router} />
  </ThemeProvider></QueryClientProvider>, { container });
  return { router, requests, client, recoverFileRead: () => { fileReadFailed = false; }, failFileRead: () => { fileReadFailed = true; } };
}
