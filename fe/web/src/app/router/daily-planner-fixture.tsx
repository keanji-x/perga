import { Profiler,StrictMode,type ReactNode } from 'react';
import type { SessionIdentity } from '../../../../core/api/auth.ts';
import type { AgentProvider } from '../../../../core/domain/presentation-types.ts';
import { prepareReportDocument,type ReportPreparationPort } from '../../features/report/document/preparation.ts';
import { SessionGate } from '../auth/session-gate.tsx';
import { conversationCardId } from './conversation-read-data.ts';
import { sessionPage } from './conversation-session-data.ts';
import { fixtureBootstrap,fixtureReport,fixtureSession,fixtureSnapshot,fixtureTrack } from './session-contract-fixture.ts';
// Test-only transport for the complete daily homepage composition; no model or live API.
import { QueryClient,QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider,createMemoryHistory,type AnyRoute,type RouterHistory } from '@tanstack/react-router';
import { render } from '@testing-library/react';
import type { ApiRequest,ApiTransportPort,ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { trackReportLinkUrl } from '../../../../core/domain/report.ts';
import { withReadReceipts,type ReadReceiptsKernel } from '../providers/read-receipts-fixture.tsx';
import type { UiPreferences } from '../providers/ui-preferences.tsx';
import { ThemeProvider } from '../theme/public.tsx';
import { createAppRouter } from './public.tsx';
import { bootTestCardRuntime } from './test-card-runtime.ts';

export function renderDailyFixture({ history, identity, routeProbe, withTransport, strict = false, initial = '/', plannerProvider = 'codex', failChanges = false, emptyWorkspace = false, extraPlanner = false, projectTitle = 'Project evidence', reportBody, reply, onCommit, uiPreferences, receipts, prepareReport = prepareReportDocument, }: Readonly<{ history?: RouterHistory; identity?: SessionIdentity; routeProbe?: ReactNode; withTransport?: (transport: ApiTransportPort) => ApiTransportPort; strict?: boolean; prepareReport?: ReportPreparationPort; plannerProvider?: AgentProvider; uiPreferences?: UiPreferences; receipts?: ReadReceiptsKernel; initial?: string; failChanges?: boolean; emptyWorkspace?: boolean; extraPlanner?: boolean; projectTitle?: string; reportBody?: string; reply?: (request: ApiRequest) => ApiTransportResponse | undefined | Promise<ApiTransportResponse | undefined>; onCommit?: () => void;  }> = {}) {
  const requests: ApiRequest[] = [];
  const area = { id: 'daily-area', name: 'system', color: '#6574cd', sortOrder: 0, kind: 'system', createdAtMs: 1, updatedAtMs: 1 };
  const projectArea = { ...area, id: 'project-area', name: 'Project', kind: 'user' };
  const track = fixtureTrack('daily', { title: 'Renamed daily Track', leadSessionId: 'daily-planner', ...({ workspace: { isolation: 'none', kind: 'attached', path: '/tmp', frozenAt: null } }) });
  const project = fixtureTrack('project', { areaId: projectArea.id, title: projectTitle });
  const card = fixtureSession('daily-planner', track.id, { title: 'Daily Planner conversation', isLead: true, provider: plannerProvider });
  const sessions = [card, ...(extraPlanner ? [fixtureSession('second-planner', track.id, { title: 'Other conversation', provider: plannerProvider })] : [])];
  const report = fixtureReport(reportBody ?? `# 今日计划\n\nPrioritize the release. Read [Project evidence](${trackReportLinkUrl(project.id)}).\n`);
  const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
  const transport: ApiTransportPort = { send: async (request) => {
    await Promise.resolve();
    requests.push(request);
    const response = await reply?.(request);
    if (response !== undefined) return response;
    if (request.path === '/api/v1/auth/session' && identity !== undefined) return ok(identity);
    if (request.path.startsWith('/api/v1/bootstrap')) return ok(fixtureBootstrap([plannerProvider]));
    if (request.path.includes('/workspace-options')) return ok({ defaultId: 'direct', options: [{ id: 'direct', label: 'Direct folder', description: 'Use this folder' }] });
    if (request.path === '/api/v1/areas') return ok(emptyWorkspace ? [] : [projectArea]);
    if (request.path === '/api/v1/areas/daily-area/tracks?includeClosed=true') return ok([track]);
    if (request.path === '/api/v1/areas/project-area/tracks?includeClosed=true') return ok([project]);
    if (request.path.startsWith('/api/v1/today/daily')) {
      const date = new URL(request.path, 'http://fixture').searchParams.get('date');
      return ok(date === null || date === '2026-10-04' ? { date: '2026-10-04', timeZone: 'Asia/Shanghai', trackId: track.id } : null);
    }
    if (request.path.startsWith('/api/v1/today/calendar')) {
      const params = new URL(request.path, 'http://fixture').searchParams;
      const start = params.get('start') ?? '2026-09-28';
      const end = params.get('end') ?? '2026-10-04';
      const days: Array<{ date: string; hasDailyTrack: boolean; hasReport: boolean }> = [];
      const cursor = new Date(`${start}T12:00:00Z`);
      const limit = new Date(`${end}T12:00:00Z`);
      while (cursor <= limit && days.length < 62) {
        const day = cursor.toISOString().slice(0, 10);
        const hasDaily = day === '2026-10-04';
        days.push({
          date: day,
          hasDailyTrack: hasDaily,
          hasReport: hasDaily && (reportBody === undefined || reportBody.trim().length > 0),
        });
        cursor.setUTCDate(cursor.getUTCDate() + 1);
      }
      return ok({ today: '2026-10-04', timeZone: 'Asia/Shanghai', start, end, days, schedules: [] });
    }
    if (request.path === '/api/v1/tracks/daily') return ok(fixtureSnapshot(track, sessions, report));
    if (request.path === '/api/v1/tracks/daily/sessions') return ok(sessions);
    if (request.path === '/api/v1/tracks/project') return ok(fixtureSnapshot(project, [], fixtureReport('# Project result\n\nRelease is ready.\n')));
    if (request.path.startsWith('/api/v1/today/report-changes')) return failChanges
      ? { status: 500, statusText: 'Error', body: { error: 'Report history unavailable' } }
      : ok({ date: '2026-10-03', timezone: 'Asia/Shanghai', throughEventId: 42, nextCursor: null, changes: [{ trackId: project.id, trackTitle: project.title, areaId: projectArea.id, areaName: projectArea.name, editCount: 2, firstEventId: 10, lastEventId: 42, summaryBefore: 'In progress', summaryAfter: 'Ready', patch: '--- a/report.md\n+++ b/report.md\n@@ -1 +1 @@\n-Building\n+Release is ready.\n', patchTruncated: false }] });
    if (request.path.startsWith('/api/v1/today/report-edits')) return ok({ nextCursor: null, edits: [{ eventId: 42, at: Date.parse('2026-10-03T01:00:00Z'), edit: { trackId: project.id, editId: 'edit-42', summaryBefore: 'In progress', summaryAfter: 'Ready', bodyBefore: '# Before release\n\nBuilding', bodyAfter: '# After release\n\nRelease is ready.' } }] });
    if (request.path.includes('/sessions/') && request.path.includes('/items?')) return ok(sessionPage(sessions.find(session => session.id === conversationCardId(request.path)) ?? card));
    if (request.path.startsWith('/api/v1/overlays?')) return ok({ tracks: [] });
    if (request.path === '/api/plugins') return ok([]);
    if (request.path === '/api/settings') return ok({});
    return ok([]);
  } };
  const unauthorized = createUnauthorizedChannel({ enqueue: (task) => task() });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const fixtureTransport = withReadReceipts(transport, receipts);
  const router = createAppRouter({ prepareReport, transport: withTransport?.(fixtureTransport) ?? fixtureTransport, unauthorized, client, cards: bootTestCardRuntime(), uiPreferences, onSignOut: () => undefined });
  // Observe commits inside the existing provider via declared child routes. Each
  // wrapper delegates the original component and adds only a nonvisual test probe.
  if (routeProbe !== undefined) for (const route of (router.routeTree.children ?? []) as readonly AnyRoute[]) {
    const Component = route.options.component;
    if (Component !== undefined) route.update({ component: () => <><Component />{routeProbe}</> });
  }
  router.update({ history: history ?? createMemoryHistory({ initialEntries: [initial] }) });
  const container = document.createElement('div');
  container.id = 'root';
  document.body.appendChild(container);
  const content=<RouterProvider router={router}/>;
  const authenticated=identity === undefined ? content : <SessionGate transport={fixtureTransport} unauthorized={unauthorized} client={client}
    runtime={{deleteDatabase:()=>undefined,idbDatabaseName:'fixture'}} cursorStore={{clear:()=>undefined}}
    renderLogin={()=><p>Sign in required</p>} renderError={()=><p>Identity unavailable</p>}>{content}</SessionGate>;
  const tree = <Profiler id="daily-fixture" onRender={onCommit ?? (() => undefined)}><QueryClientProvider client={client}><ThemeProvider storage={{ getItem: () => null, setItem: () => undefined }}>{authenticated}</ThemeProvider></QueryClientProvider></Profiler>;
  render(strict ? <StrictMode>{tree}</StrictMode> : tree, { container });
  return { router, requests, client };
}
