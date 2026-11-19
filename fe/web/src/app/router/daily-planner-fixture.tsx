import { sessionPage } from './conversation-session-data.ts';
import { fixtureTrack, fixtureSession, fixtureSnapshot, fixtureReport, fixtureBootstrap } from './session-contract-fixture.ts';
import type { SessionIdentity } from '../../../../core/api/auth.ts';
import { SessionGate } from '../auth/session-gate.tsx';
import { conversationCardId } from './conversation-read-data.ts';
import { prepareReportDocument, type ReportPreparationPort } from '../../features/report/document/preparation.ts';
import type { AgentProvider } from '../../../../core/api/generated/wire.ts';
import { Profiler, StrictMode, type ReactNode } from 'react';
// Test-only transport for the complete daily homepage composition; no model or live API.
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider, createMemoryHistory, type AnyRoute, type RouterHistory } from '@tanstack/react-router';
import { render } from '@testing-library/react';
import type { ApiRequest, ApiTransportPort, ApiTransportResponse } from '../../../../core/api/types.ts';
import { trackReportLinkUrl } from '../../../../core/domain/report.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { createAppRouter } from './public.tsx';
import { bootTestCardRuntime } from './test-card-runtime.ts';
import type { UiPreferences } from '../providers/ui-preferences.tsx';
import { ThemeProvider } from '../theme/public.tsx';
import { withReadReceipts, type ReadReceiptsKernel } from '../providers/read-receipts-fixture.tsx';

export function renderDailyFixture({ history, identity, routeProbe, withTransport, strict = false, initial = '/', plannerProvider = 'codex', failChanges = false, emptyWorkspace = false, extraPlanner = false, projectTitle = 'Project evidence', reportBody, reply, onCommit, uiPreferences, receipts, prepareReport = prepareReportDocument, pergaMode }: Readonly<{ history?: RouterHistory; identity?: SessionIdentity; routeProbe?: ReactNode; withTransport?: (transport: ApiTransportPort) => ApiTransportPort; strict?: boolean; prepareReport?: ReportPreparationPort; plannerProvider?: AgentProvider; uiPreferences?: UiPreferences; receipts?: ReadReceiptsKernel; initial?: string; failChanges?: boolean; emptyWorkspace?: boolean; extraPlanner?: boolean; projectTitle?: string; reportBody?: string; reply?: (request: ApiRequest) => ApiTransportResponse | undefined | Promise<ApiTransportResponse | undefined>; onCommit?: () => void; pergaMode?: boolean }> = {}) {
  const requests: ApiRequest[] = [];
  const area = { id: 'daily-area', name: 'system', color: '#6574cd', sortOrder: 0, kind: 'system', createdAtMs: 1, updatedAtMs: 1 };
  const projectArea = { ...area, id: 'project-area', name: 'Project', kind: 'user' };
  const track = fixtureTrack('daily', { title: 'Renamed daily Track', leadSessionId: 'daily-planner', ...(pergaMode ? { workspace: { isolation: 'none', kind: 'attached', path: '/tmp', frozenAt: null } } : {}) });
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
    if (request.path === '/api/v1/bootstrap?scope=whoami' && identity !== undefined) return ok(identity);
    if (request.path.startsWith('/api/v1/bootstrap') && !request.path.includes('scope=whoami')) return ok(fixtureBootstrap([plannerProvider]));
    if (request.path === '/api/v1/areas') return ok(emptyWorkspace ? [] : [projectArea]);
    if (request.path === '/api/v1/areas/daily-area/tracks?includeClosed=true') return ok([track]);
    if (request.path === '/api/v1/areas/project-area/tracks?includeClosed=true') return ok([project]);
    if (request.path.startsWith('/api/today/daily')) {
      const date = new URL(request.path, 'http://fixture').searchParams.get('date');
      return ok(date === null || date === '2026-10-04' ? { date: '2026-10-04', time_zone: 'Asia/Shanghai', track_id: track.id } : null);
    }
    if (request.path === '/api/v1/tracks/daily') return ok(fixtureSnapshot(track, sessions, report));
    if (request.path === '/api/v1/tracks/daily/sessions') return ok(sessions);
    if (request.path === '/api/v1/tracks/project') return ok(fixtureSnapshot(project, [], fixtureReport('# Project result\n\nRelease is ready.\n')));
    if (request.path.startsWith('/api/today/report-changes')) return failChanges
      ? { status: 500, statusText: 'Error', body: { error: 'Report history unavailable' } }
      : ok({ date: '2026-10-03', timezone: 'Asia/Shanghai', through_event_id: 42, next_cursor: null, changes: [{ track_id: project.id, track_title: project.title, area_id: projectArea.id, area_name: projectArea.name, edit_count: 2, first_event_id: 10, last_event_id: 42, summary_before: 'In progress', summary_after: 'Ready', patch: '--- a/report.md\n+++ b/report.md\n@@ -1 +1 @@\n-Building\n+Release is ready.\n', patch_truncated: false }] });
    if (request.path.startsWith('/api/today/report-edits')) return ok({ next_cursor: null, edits: [{ event_id: 42, at: Date.parse('2026-10-03T01:00:00Z'), edit: { track_id: project.id, edit_id: 'edit-42', summary_before: 'In progress', summary_after: 'Ready', body_before: '# Before release\n\nBuilding', body_after: '# After release\n\nRelease is ready.' } }] });
    if (request.path.includes('/sessions/') && request.path.includes('/items?')) return ok(sessionPage(sessions.find(session => session.id === conversationCardId(request.path)) ?? card));
    if (request.path === '/api/plugins') return ok([]);
    if (request.path === '/api/settings') return ok({});
    return ok([]);
  } };
  const unauthorized = createUnauthorizedChannel({ enqueue: (task) => task() });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const fixtureTransport = withReadReceipts(transport, receipts);
  const router = createAppRouter({ prepareReport, transport: withTransport?.(fixtureTransport) ?? fixtureTransport, unauthorized, client, cards: bootTestCardRuntime(), uiPreferences, pergaMode, onSignOut: () => undefined });
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
