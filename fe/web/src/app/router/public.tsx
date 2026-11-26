import { createDraftBackOwner,DraftBackLifecycle,type DraftBackContext } from './draft-back-owner.tsx';
// Code-based TanStack Router setup, built inside a factory so a test can inject the
// transport and QueryClient; also the composition point for route-owned surfaces.

import type { QueryClient } from '@tanstack/react-query';
import type { TrackAggregateSnapshotDto } from '../../../../core/api/perga-v1.ts';
import {
createRootRouteWithContext,createRoute,createRouter,
useLocation,
type AnyRoute
} from '@tanstack/react-router';
import type { ReportPreparationPort } from '../../features/report/document/preparation.ts';
import { TrackViewProvider } from './track-view-state.tsx';

import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import {
ConversationProvider
} from '../conversations/public.tsx';
import { useCurrentUser, useSignOut } from '../auth/current-user.tsx';
import { ReadReceiptsProvider } from '../providers/read-receipts.tsx';
import {
createRecentFileHistory,type RecentFileHistory,
} from '../providers/recent-files.ts';
import { createUiPreferences,UiPreferencesProvider,type UiPreferences } from '../providers/ui-preferences.tsx';
import { prefetchAreaList } from '../providers/workspace-queries.ts';
import { AppShell,MobileConversationProvider } from '../shell/public.tsx';
import { useCompactViewport } from '../../ui/viewport/public.ts';
import { DailyTodayRoute } from './daily-planner.tsx';
import { MemoryRoute } from './memory-route.tsx';
import { MobileFocusRoute } from './mobile-focus-route.tsx';
import {
pathFor,useGo,useRouteParam,
validateTrackSearch,type TrackSearch
} from './navigation.ts';
import { NewTrackDraftProvider } from './new-track-drafts.tsx';
import { NewTrackRoute } from './new-track-route.tsx';

import type { CardRuntime } from './card-runtime.ts';
import { TrackRouteBody } from './track-route-body.tsx';
import { TrackRoute } from './track-route.tsx';
export const APP_BASEPATH = '/';

export type AppRouterDeps = Readonly<{
  basepath?: string;
  transport: ApiTransportPort;
  unauthorized: UnauthorizedChannel;
  client: QueryClient;

  cards: CardRuntime;
  prepareReport: ReportPreparationPort;
  recentFiles?: RecentFileHistory;
  uiPreferences?: UiPreferences;
  submittedStorage?: () => Storage;
  shareScope?: unknown;
}>;

function renderNothing(): null { return null; }

export function createRouteTree(deps: AppRouterDeps): AnyRoute {
  const { transport, unauthorized, client, cards, prepareReport } = deps;
  const shareScope = deps.shareScope ?? null;
  const recentFiles = deps.recentFiles ?? createRecentFileHistory();
  const preferences = deps.uiPreferences ?? createUiPreferences();
  const renderMemoryTrack = (snapshot: TrackAggregateSnapshotDto, areaId: string | null) =>
    <TrackRouteBody key={snapshot.track.id} snapshot={snapshot} memoryAreaId={areaId}
      trackPath={pathFor(areaId === null ? { name: 'memory' } : { name: 'area-memory', areaId })}
      showWorkspaceIsolation={false} workspaceWaiting={snapshot.workspacePreparation != null}
      prepareReport={prepareReport} transport={transport} unauthorized={unauthorized}
      cardRuntime={cards} recentFiles={recentFiles} />;
  const rootRoute = createRootRouteWithContext<DraftBackContext>()({ component: () => (
    <UiPreferencesProvider preferences={preferences}><ReadReceiptsProvider transport={transport} unauthorized={unauthorized}>
      <TrackViewProvider><DraftBackLifecycle><MobileConversationProvider><ShellRoute submittedStorage={deps.submittedStorage} transport={transport} unauthorized={unauthorized} shareScope={shareScope} /></MobileConversationProvider></DraftBackLifecycle></TrackViewProvider>
    </ReadReceiptsProvider></UiPreferencesProvider>
  ) });

  const indexRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/',
    /** The index loader primes only the areas list; awaiting the area → tracks fan-out here would let one slow area block the route commit. */
    loader: () => prefetchAreaList(client, transport, unauthorized),
    validateSearch: (search: Record<string, unknown>) => ({ day: typeof search.day === 'string' ? search.day : undefined }),
    component: function DailyRoute() {
      const compact = useCompactViewport();
      const day = new URLSearchParams(useLocation({ select: (location) => location.searchStr })).get('day') ?? undefined;
      const go = useGo();
      if (compact && day === undefined) {
        return <MobileFocusRoute transport={transport} unauthorized={unauthorized} shareScope={shareScope} />;
      }
      return <DailyTodayRoute transport={transport} unauthorized={unauthorized} selectedDate={day}
        onOpenTrack={(trackId) => go({ name: 'track', trackId })}
        onSelectDate={(nextDay) => go({ name: 'today', day: nextDay })}
        renderTrack={(detail, evidence, panelContent, intro, supervisor, center) => <TrackRouteBody memoryAreaId={shareScope === null ? null : false} showWorkspaceIsolation={false} center={center} reportHeader={supervisor} reportEmpty={null} mobileReportIntro={intro} panelContent={panelContent} reportEvidence={evidence} key={detail.track.id} prepareReport={prepareReport} transport={transport} unauthorized={unauthorized}
          snapshot={detail} cardRuntime={cards} recentFiles={recentFiles} />} />;
    },
  });

  const globalMemoryRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/memory',
    validateSearch: validateTrackSearch,
    component: () => <MemoryRoute transport={transport} unauthorized={unauthorized} areaId={null} renderTrack={renderMemoryTrack} />,
  });

  const areaMemoryRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/area/$areaId/memory',
    validateSearch: validateTrackSearch,
    component: function AreaMemoryRoute() {
      const areaId = useRouteParam('/area/');
      return areaId === undefined ? null : <MemoryRoute key={areaId} transport={transport} unauthorized={unauthorized} areaId={areaId} renderTrack={renderMemoryTrack} />;
    },
  });

  const newTrackRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/area/$areaId/new',
    component: () => <NewTrackRoute transport={transport} unauthorized={unauthorized} memoryEnabled={shareScope === null} />,
  });

  const trackRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/track/$trackId',
    validateSearch: (search: Record<string, unknown>): TrackSearch => validateTrackSearch(search),
    component: () => <TrackRoute
      memoryEnabled={shareScope === null}
      prepareReport={prepareReport}
      transport={transport}
      unauthorized={unauthorized}
      cardRuntime={cards}
      recentFiles={recentFiles}
    />,
  });

  /* Every settings route renders nothing: the URL is the state and `SettingsOverlay`
       is its view. A route component remounts on every navigation, which would replay
       the panel's entrance animation on every click. */
  const settingsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/settings',
    component: renderNothing,
  });

  const plannersRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/settings/planners',
    component: renderNothing,
  });

  const appearanceRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/settings/appearance',
    component: renderNothing,
  });

  const aboutRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/settings/about',
    component: renderNothing,
  });
  return rootRoute.addChildren([
    indexRoute, newTrackRoute, trackRoute, settingsRoute,
    plannersRoute, appearanceRoute, aboutRoute,
    ...(shareScope === null ? [globalMemoryRoute, areaMemoryRoute] : []),
  ]);
}

export function createAppRouter(deps: AppRouterDeps) {
  const router = createRouter({
    routeTree: createRouteTree(deps),
    basepath: deps.basepath ?? APP_BASEPATH,
    defaultPreload: false,
    // Report arrivals reveal only after their current complete content commits.
    defaultHashScrollIntoView: false,
    // App scroll ports and report arrivals own scrolling, including POP to the
    // initial history entry (which has no navigation-time hash-scroll options).
    scrollRestoration: () => false,
  });
  router.update({ context: { draftBack: createDraftBackOwner(router) } });
  return router;
}

function ShellRoute({ transport, unauthorized, submittedStorage, shareScope }: { submittedStorage?: () => Storage; transport: ApiTransportPort; unauthorized: UnauthorizedChannel; shareScope: unknown }) {
  const go = useGo();
  const currentUser = useCurrentUser();
  const signOut = useSignOut();
  return (
    <ConversationProvider submittedStorage={submittedStorage}><NewTrackDraftProvider submittedStorage={submittedStorage}>
      <AppShell
        shareScope={shareScope}
        transport={transport}
        unauthorized={unauthorized}
        onOpenSettings={() => go({ name: 'settings' })}
        onOpenPlugins={undefined}
        userLabel={currentUser?.displayName}
        onSignOut={currentUser !== null && signOut !== null ? () => signOut(currentUser.sessionId) : undefined}
      />
    </NewTrackDraftProvider></ConversationProvider>
  );
}
