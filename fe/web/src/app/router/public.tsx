import { priorityConversation } from '../../../../core/domain/activity.ts';
import { trackSchedulesOperation, setTrackScheduleOperation, cancelTrackScheduleOperation, triggerTrackScheduleOperation, trackTemplateOperation, updateTrackTemplateInputsOperation } from '../../../../core/domain/track-schedules.ts';
import { trackSchedules, trackTemplate, trackDetail } from '../../../../core/events/query-keys.ts';
import { useRecoveryMutation } from '../providers/recovery-mutation.ts';
import type { ApiOperation } from '../../../../core/api/types.ts';
import type { TrackAggregateSnapshotDto } from '../../../../core/api/schemas.ts';
import { TemplateProtocolBar } from '../../features/report/template-protocol/public.tsx';
import { ShareScopeProvider, useShareScope } from '../shell/public.tsx';
import { terminalPresentation } from '../../../../core/domain/terminal-presentation.ts';
import type { ShareScope } from '../../../../core/api/generated/wire.ts';
import { TrackShareLinks } from '../providers/share-management.tsx';
import { useShareManagement } from '../shell/public.tsx';
import { createDraftBackOwner, DraftBackLifecycle, type DraftBackContext } from './draft-back-owner.tsx';
import { usePlannerSwitchControl } from './planner-switch-control.ts';
import { composerCommands, conversationCommands, conversationCommandBindings, type CommandBindings } from '../../features/chat/thread/composer-command.ts';
import { trackCommandBindings } from './track-composer-command.ts';
import { useTrackClosedControl } from '../../features/track/page/closed-control.ts';
import { TodayReportEmpty } from '../../features/today/public.tsx';
import { PanelModule } from '../../ui/panel-card/public.tsx';
import type { DialogOrigin } from '../../ui/dialog/public.tsx';
import { PendingQueue } from '../../features/chat/thread/pending-queue.tsx';
import { useConversationLinks } from './conversation-links.tsx';
import { useTerminalWorkbench, TerminalWorkbenchView, type TerminalWorkbench } from '../../features/track/workbench/public.tsx';
import { TerminalCompanion } from './terminal-companion.tsx';
import { managedProvidersQueryOptions, useManagedProviders } from '../providers/managed-providers.ts';
import { useThreadReadingView } from '../../features/chat/thread/reading-view.tsx';
import { useContentAdmission } from '../../ui/content-presentation/public.ts';
import { useConversationDraftRetention, useConversationPaneTarget, useRequestedConversationOpen, useConversationEscape, useConversationDraftAutoSend } from '../conversations/pane-lifecycle.ts';
import { createConversationDraftActions } from '../conversations/draft-actions.ts';
import { useConversationStore } from '../conversations/store.ts';
import { ConversationApprovalControl } from '../conversations/approval-control.tsx';
import type { ConversationCreationSource, ConversationRouteIntent, PlannerConversationScope } from '../conversations/contracts.ts';
import { Button } from '@astryxdesign/core/Button';
import type { PaneResizeGroup } from '../../ui/drawer/resize-group.ts';
import { sideConversationSnapshot } from '../../../../core/domain/side-conversation.ts';
import { writeClipboardText } from '../../ui/operation-feedback/clipboard.ts';
import { deriveConversationAction, conversationCommandEnabled } from '../../../../core/domain/conversation-action.ts';
import { ConversationSessionHeader } from '../../features/chat/thread/session-header.tsx';
import { readErrorText } from '../../../../core/domain/read-failure.ts';
import { permissionModesForProvider } from '../../../../core/domain/agent-providers.ts';
import { permissionModeAfterProviderChange, resolvePermissionControl } from '../../../../core/domain/planner-permission-mode.ts';
import { DELETE_FAILURES, DELETE_TEXT, writeFailureText } from '../../../../core/domain/failure-class.ts';
// Code-based TanStack Router setup, built inside a factory so a test can inject the
// transport and QueryClient; also the composition point for route-owned surfaces.

import {
  createRootRouteWithContext, createRoute, createRouter, redirect, useLocation, useRouterState, type AnyRoute,
} from '@tanstack/react-router';
import { createRef, useCallback, useEffect, useLayoutEffect, useMemo, useRef, type Dispatch, type ReactNode, type SetStateAction } from 'react';
import { TrackViewProvider, useTrackViewState, useTrackViews } from './track-view-state.tsx';
import { TrackReportRegion, useTrackReportPresentation, useTrackReportArrival } from './track-report-presentation.tsx';
import type { ReportPreparationPort } from '../../features/report/document/preparation.ts';
import { TrackPending } from '../../features/track/page/pending.tsx';
import { HStack } from '@astryxdesign/core/HStack';
import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';

import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { AgentProvider, AskAnswer } from '../../../../core/api/generated/wire.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import {
  ATTACHED_WORKSPACE_REASON, PlannerAttachButton, PlannerAttachmentDrawer,
  type AttachmentStore, usePlannerAttachments,
} from '../../features/planner/attachments.tsx';
import {
  toTrack, trackActivityFromSnapshot, trackDisplayTitle,
  trackDtoToWire, retryTrackWorkspaceOperation,
} from '../../../../core/domain/track.ts';
import { cardActivityOf, type CardActivity } from '../../../../core/domain/activity.ts';
import { parkedWorkerNotices, workbenchTerminalCardIds } from '../../../../core/view/track-page.ts';
import type {
  BoardHostItem, CardAddMenuEntry, CardHost, CardRegistry,
} from '../../systems/cards/public.js';
import {
  cardAddMenuEntries, createCardHost, partitionTrackCards,
} from '../../systems/cards/public.js';
import { mintIdempotencyKey, useKeyedIntent, type KeyedRequest } from '../providers/idempotency-key.ts';
import footerStyles from './composer-footer.module.css';
import { TrackPage } from '../../features/track/page/public.tsx';
import { CardGridOverlay, TrackStage } from '../../features/track/grid/public.tsx';
import { AddCardMenu, NewCardForm, type NewCardValues } from '../../features/track/new-card/public.tsx';
import {
  cardCreateEnded, cardCreateFailureText, keyedCardBodyOf, sameCardDraft, sendCardCreate,
  type CardCreatePort, type CardDraft, type KeyedCardBody,
} from '../../features/track/new-card/create.ts';
import { ChatList } from '../../features/chat/list/public.tsx';
import { OutgoingMessage } from '../../features/chat/thread/outgoing-message.tsx';
import {
  ChatFooterError, ChatFooterNotice, ChatFooterRemedy, ChatThread, ChatEmpty, type ComposerFocusHandle,
} from '../../features/chat/thread/public.tsx';
import { ModelPill, PermissionModePill, PlannerRolePill } from '../../features/chat/thread/model-pill.tsx';
import { usePlannerRoleControl, type PlannerRoleControl } from './planner-role.ts';
import { ContextRing } from '../../features/chat/thread/context-ring.tsx';
import { useMentionTrigger } from '../../features/chat/thread/mention-trigger.tsx';
import { ReportBacklinks } from '../../features/report/backlinks/public.tsx';
import { TaskRecovery, useCurrentTaskRows } from './task-recovery.tsx';
import { WorkerDispatchControl } from './worker-controls.tsx';
import { useReportPreviewResolver, useReportPreviewViewports } from './report-preview.ts';
import { useReportSeriesResolver } from './report-series.ts';
import { ReportReferencePreview } from './report-reference.tsx';
import { ReportSourceInDocument, ReportSourcePreview } from './report-source.tsx';
import { reportSourcePosition, type ReportSourcePosition } from '../../features/report/source/placement.ts';
import { ReportEmpty } from '../../features/report/empty/public.tsx';
import { ReportFileViewer } from '../../features/report/file-viewer/public.tsx';
import { ReportOutline } from '../../features/report/outline/public.tsx';
import { RecentFiles } from '../../features/report/recent-files/public.tsx';
import {
  backlinkCountsByBlock, deriveReportTasks, type ReportLinkTarget,
} from '../../../../core/domain/report.ts';
import {
  parseWorkspaceRelativeFilePath, type ReportFileLinkTarget,
} from '../../../../core/domain/report-file.ts';
import type { ReportSourceLinkTarget } from '../../../../core/domain/report-source.ts';
import {
  conversationName,
  isUserConversationTurn,
  FOLLOW_INSTALLATION_DEFAULT,
  type Conversation,
  type ConversationTurn,
  type PendingQueueEntry,
  type TranscriptEntry,
} from '../../../../core/domain/conversation.ts';
import { Dialog } from '../../ui/dialog/public.tsx';
import { createDirectoryLister, createTrackCardFilesPort, createTrackWorkspaceFilesPort } from '../providers/directory.ts';
import { useMentionSearch } from '../providers/mentions.ts';
import { DELETE_CARD_COPY } from '../../ui/confirm-dialog/copy.ts';
import { OperationFeedback, DeleteConfirmDialog, useDeleteConfirm, useOperationFeedback } from '../../ui/operation-feedback/public.tsx';
import { useCommittedCallback } from '../../ui/state/committed-callback.ts';
import { useState } from '../../ui/state/public.ts';
import {
  serverVersionOperation, runOperation,
  prefetchAreaList, areaListQueryOptions, tracksInAreaQueryOptions,

  useTrackConversationMutations, useTrackMutations,
  trackBacklinksQueryOptions, trackDetailQueryOptions, trackToolsQueryOptions,
  trackTaskVerdictsQueryOptions,
} from '../providers/queries.ts';
import { NewTrackRoute } from './new-track-route.tsx';
import { DailyTodayRoute, type DailyCenterPresentation } from './daily-planner.tsx';
import { ConversationComposer, ConversationDock } from './conversation-composer.tsx';
import mobileChatStyles from './mobile-chat.module.css';
import dailyStyles from './daily-planner.module.css';
import { ConversationSurface, type EmbeddedConversationPresentation } from './mobile-conversation-surface.tsx';
import { agentSessionDtoToConversation } from '../../../../core/domain/conversation.ts';
import { NewTrackDraftProvider } from './new-track-drafts.tsx';
import { RecipesRoute } from './recipes-route.tsx';
import { createUiPreferences, UiPreferencesProvider, useUiPreferences, type UiPreferences } from '../providers/ui-preferences.tsx';
import { ReadReceiptsProvider, useReadReceipt, useReadReceipts, useViewReadReceipt } from '../providers/read-receipts.tsx';
import { TrackSelector } from '../shell/track-selector.tsx';
import { AppShell, MobileConversationProvider, useMobileConversationOwner, useConversationDrawerResize, useOpenMobileSection, useMobileHeaderActionsHost, useMobileHeaderTitleHost } from '../shell/public.tsx';
import {
  ConversationProvider, useConversationCommands, useConversationAttachments, useConversationDraft, useConversationOpenRequest, usePendingTrackInitialSend, useConversationUpload, type ConversationDraftId,
} from '../conversations/public.tsx';
import {
  renderedMobilePanel,
  useGo, useGoSameTrack, useRouteCardId, useRouteFilePath, useRouteFrom, useRouteHash,
  useRoutePanel, useRouteParam, useTrackFileNavigation,
  usePlannerOpenIntent, useTrackPanelNavigation, validateTrackSearch, type TrackSearch,
} from './navigation.ts';
import {
  createRecentFileHistory, type RecentFileHistory,
} from '../providers/recent-files.ts';
import { readHostThemeRgb } from '../theme/host-rgb.ts';
import { PendingRoute } from './pending-route.tsx';
import { Banner } from '@astryxdesign/core/Banner';
import { ErrorBox } from '../../ui/error-box/public.tsx';
import { ApprovalDrawer, PlannerAskDrawer } from '../../features/planner/public.ts';
import { approvalsForSession, NO_PENDING_APPROVALS, type PendingApproval } from '../../../../core/domain/approval.ts';
import { openAsksOf, type OpenAsk } from '../../../../core/domain/ask.ts';
import { useCompactViewport } from '../../ui/viewport/public.ts';

export const APP_BASEPATH = '/next';

/** One stable value for a conversation without pending questions. */
const NO_ASKS: readonly OpenAsk[] = Object.freeze([]);

/** A route-owned server list whose rows open in this panel's drawer. */
type ConversationPanelSource = ConversationCreationSource & Readonly<{
    /** The Track this draft belongs to. */
    scopeId: string;
    rows: readonly Conversation[];
    /** Default provider to seed new subagent drafts on this Track. */
    defaultProvider?: AgentProvider;
    /** Whether this surface runs against Perga v1 endpoints. */
    pergaMode?: boolean;
    /** The kernel's per-card verdicts for the track these rows are on; required, because a caller that passes no overlay has a list on which nothing can ever be working. */
    cards: Readonly<Record<string, CardActivity>>;
    /** The Track these rows may be sent to. */
    rememberOn: string;
    /** Known on the Track route; Today resolves the open conversation's Track on demand. */
    workspaceRoot: string | null;
    scopeOf: (conversationId: string) => PlannerConversationScope | null;
    /** Live execution-scoped approvals for this Track, scoped to each owning sessionId by the pane. */
    pendingApprovals: readonly PendingApproval[];
    decideApproval: (approval: PendingApproval, optionId: string) => Promise<void>;
    /** The row and Area supplying report mention candidates; null without an Area context.
     * Candidate inserts are server-owned and the composer sends them verbatim.
     * asks/answerAsk serve durable open asks above the composer; role supplies the Area admin state. */
    planner: Readonly<{
      cardId: string; areaId: string; asks: readonly OpenAsk[];
      answerAsk: (askId: import('../../../../core/domain/activity.ts').AskId, answers: readonly AskAnswer[]) => Promise<void>;
      role: PlannerRoleControl;
      commands?: (stop: (() => void) | undefined) => CommandBindings;
    }> | null;
  }>;

/** The card runtime, created once at boot and injected. */
export type CardRuntime = Readonly<{ registry: CardRegistry; host: CardHost }>;

export type AppRouterDeps = Readonly<{
  basepath?: string;
  shareScope?: ShareScope | null;
  shareLinkId?: string;
  transport: ApiTransportPort;
  unauthorized: UnauthorizedChannel;
  client: QueryClient;
  onSignOut: () => void;
  pergaMode?: boolean;
  cards: CardRuntime;
  prepareReport: ReportPreparationPort;
  recentFiles?: RecentFileHistory;
  uiPreferences?: UiPreferences;
  submittedStorage?: () => Storage;
}>;

function renderNothing(): null { return null; }

export function createRouteTree(deps: AppRouterDeps): AnyRoute {
  const { transport, unauthorized, client, onSignOut, pergaMode = false, cards, prepareReport } = deps;
  const recentFiles = deps.recentFiles ?? createRecentFileHistory();
  const preferences = deps.uiPreferences ?? createUiPreferences();
  const rootRoute = createRootRouteWithContext<DraftBackContext>()({ component: () => (
    <ShareScopeProvider value={deps.shareScope == null || deps.shareLinkId === undefined ? null : { scope: deps.shareScope, shareLinkId: deps.shareLinkId }}><UiPreferencesProvider preferences={preferences}><ReadReceiptsProvider transport={transport} unauthorized={unauthorized}>
      <TrackViewProvider><DraftBackLifecycle><MobileConversationProvider><ShellRoute submittedStorage={deps.submittedStorage} shareScope={deps.shareScope ?? null} transport={transport} unauthorized={unauthorized} onSignOut={onSignOut} pergaMode={pergaMode} /></MobileConversationProvider></DraftBackLifecycle></TrackViewProvider>
    </ReadReceiptsProvider></UiPreferencesProvider></ShareScopeProvider>
  ) });

  const indexRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/',
    /** The index loader primes only the areas list; awaiting the area → tracks fan-out here would let one slow area block the route commit. */
    loader: deps.shareScope == null ? () => prefetchAreaList(client, transport, unauthorized) : async ({ location }) => {
      const scope = deps.shareScope;
      if (scope == null) return prefetchAreaList(client, transport, unauthorized);
      const target = scope.kind === 'track' ? scope.id
        : (await client.ensureQueryData(tracksInAreaQueryOptions(transport, scope.id, unauthorized)))[0]?.id;
      return redirect({ href: `${deps.basepath ?? APP_BASEPATH}${target === undefined ? `/area/${encodeURIComponent(scope.id)}/new` : `/track/${encodeURIComponent(target)}`}${location.searchStr}${location.hash ? `#${location.hash}` : ''}`, replace: true });
    },
    validateSearch: (search: Record<string, unknown>) => ({ day: typeof search.day === 'string' ? search.day : undefined }),
    component: function DailyRoute() {
      const day = new URLSearchParams(useLocation({ select: (location) => location.searchStr })).get('day') ?? undefined;
      const go = useGo();
      return <DailyTodayRoute pergaMode={pergaMode} transport={transport} unauthorized={unauthorized} selectedDate={day}
        onOpenTrack={(trackId) => go({ name: 'track', trackId })}
        onSelectDate={(nextDay) => go({ name: 'today', day: nextDay })}
        renderTrack={(detail, evidence, panelContent, intro, supervisor, center) => <TrackRouteBody showWorkspaceIsolation={false} center={center} reportHeader={supervisor} reportEmpty={pergaMode ? null : <TodayReportEmpty />} mobileReportIntro={intro} panelContent={panelContent} reportEvidence={evidence} key={detail.track.id} prepareReport={prepareReport} transport={transport} unauthorized={unauthorized}
          snapshot={detail} cardRuntime={cards} recentFiles={recentFiles} pergaMode={pergaMode} />} />;
    },
  });

  const newTrackRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/area/$areaId/new',
    component: () => <NewTrackRoute transport={transport} unauthorized={unauthorized} pergaMode={pergaMode} />,
  });

  const trackRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/track/$trackId',
    validateSearch: (search: Record<string, unknown>): TrackSearch => validateTrackSearch(search),
    component: () => <TrackRoute
      prepareReport={prepareReport}
      transport={transport}
      unauthorized={unauthorized}
      cardRuntime={cards}
      recentFiles={recentFiles}
      pergaMode={pergaMode}
    />,
  });

  const recipesRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/recipes',
    component: () => <RecipesRoute transport={transport} unauthorized={unauthorized} />,
  });

  /* Every settings route renders nothing: the URL is the state and `SettingsOverlay`
       is its view. A route component remounts on every navigation, which would replay
       the panel's entrance animation on every click. */
  const settingsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/settings',
    component: renderNothing,
  });

  const networkRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/settings/network',
    component: renderNothing,
  });

  const pluginsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/settings/plugins',
    component: renderNothing,
  });

  const plannersRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/settings/planners',
    component: renderNothing,
  });

  const mailRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/settings/mail',
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

  const legacyTodayRoute = createRoute({ getParentRoute: () => rootRoute, path: '/today/legacy',
    validateSearch: (search: Record<string, unknown>) => ({ day: typeof search.day === 'string' ? search.day : undefined }),
    beforeLoad: ({ search }) => redirect({ to: '/', search, replace: true }) });
  return rootRoute.addChildren([
    indexRoute, legacyTodayRoute, newTrackRoute, trackRoute, recipesRoute, settingsRoute,
    networkRoute, pluginsRoute, plannersRoute, mailRoute, appearanceRoute, aboutRoute,
  ]);
}

export function createAppRouter(deps: AppRouterDeps) {
  const router = createRouter({
    routeTree: createRouteTree(deps),
    basepath: deps.basepath ?? APP_BASEPATH,
    defaultPreload: false,
    // Report arrivals reveal only after their current complete content commits.
    defaultHashScrollIntoView: false,
  });
  router.update({ context: { draftBack: createDraftBackOwner(router) } });
  return router;
}

function ShellRoute({ transport, unauthorized, onSignOut, shareScope, submittedStorage, pergaMode = false }: { submittedStorage?: () => Storage; pergaMode?: boolean; shareScope: ShareScope | null; transport: ApiTransportPort; unauthorized: UnauthorizedChannel; onSignOut: () => void }) {
  const go = useGo();
  const versionQuery = useQuery({
    queryKey: ['server-version'],
    queryFn: () => runOperation(transport, serverVersionOperation(), unauthorized),
    enabled: pergaMode,
    retry: false,
  });
  const singleOwner = pergaMode && versionQuery.data?.authRequired !== true;
  return (
    <ConversationProvider submittedStorage={submittedStorage}><NewTrackDraftProvider submittedStorage={submittedStorage}>
      <AppShell shareScope={shareScope}
        transport={transport}
        unauthorized={unauthorized}
        onOpenSettings={() => go({ name: 'settings' })}
        onOpenPlugins={pergaMode ? undefined : () => go({ name: 'settings-plugins' })}
        onSignOut={singleOwner ? undefined : onSignOut}
        pergaMode={pergaMode}
      />
    </NewTrackDraftProvider></ConversationProvider>
  );
}

/**
 * The conversation module shared by Today and Track. A draft is a third open state,
 * not a `Conversation`: the card is minted by the first message, so until one is
 * sent there is no card id and nothing to fetch.
 */
type ConversationPanelOptions = Readonly<{
  defaultRowId: string | null;
  onPresent?: () => void;
  embedded?: EmbeddedConversationPresentation;
  presented?: boolean;
  onArchive?: (cardId: string, archived: boolean) => Promise<void>;
  terminal?: { workbench: TerminalWorkbench; render: () => ReactNode };
  showTrack?: boolean;
  resizable?: boolean;
  reportComposer?: Readonly<{ ready: boolean; returnLabel: string }>;
  workspace?: Readonly<{ waiting: boolean; notice?: ReactNode }>;
}>;

/** Two independent cards share the existing creation/recovery path and registry. */
function useConversationPanel(
  transport: ApiTransportPort, unauthorized: UnauthorizedChannel, source: ConversationPanelSource,
  options: ConversationPanelOptions,
) {
  const registry = useConversationCommands();
  const compact = useCompactViewport();
  const [sideError, setSideError] = useState<string | null>(null);
  const savedTarget = useConversationPaneTarget(source.scopeId, source.rows);
  // The declared default owns resources even when its surface is absent. Keep
  // that fallback out of preferences: only an explicit selection opens a Sheet.
  const declaredDefault = options.defaultRowId === null ? null : { kind: 'row' as const, id: options.defaultRowId };
  const logicalTarget = savedTarget[0] ?? declaredDefault;
  const surfaceTarget = savedTarget[0] ?? (options.embedded?.active ? logicalTarget : null);
  const mainTarget = [surfaceTarget, savedTarget[1]] as const;
  const parentId = logicalTarget?.kind === 'row' ? logicalTarget.id : null;
  const lastMainRow = useRef<string | null>(null);
  const selectedRowId = savedTarget[0]?.kind === 'row' ? savedTarget[0].id : null;
  useEffect(() => { if (selectedRowId !== null) lastMainRow.current = selectedRowId; }, [selectedRowId]);
  const receipts = useReadReceipts();
  const preferredConversation = priorityConversation(source.rows, source.cards,
    row => receipts.isUnread('conversation', row.id, row.lastTurnCompletedAt ?? null));
  const dockOwnerId = compact && options.reportComposer !== undefined
    ? selectedRowId ?? source.rows.find(row => row.id === lastMainRow.current)?.id
      ?? preferredConversation?.id ?? source.planner?.cardId ?? source.rows[0]?.id ?? null
    : parentId;
  const mainReportComposer = options.reportComposer === undefined ? undefined : { ...options.reportComposer, conversationId: dockOwnerId };
  const capabilities = useQuery({ queryKey: ['server-version'],
    queryFn: () => runOperation(transport, serverVersionOperation(), unauthorized), enabled: parentId !== null && !compact, retry: false });
  const sideSlot = `${source.scopeId}:side:${parentId ?? ''}`;
  const sideTarget = useConversationPaneTarget(sideSlot, source.rows);
  const sideDraft = useConversationDraft(sideSlot);
  const childId = sideTarget[0]?.kind === 'row' ? sideTarget[0].id : null;
  const child = source.rows.find((row) => row.id === childId);
  const sideOwnerMatches = parentId !== null && (sideTarget[0]?.kind === 'draft'
    ? sideDraft?.side?.source_card_id === parentId : child?.sourceCardId === parentId);
  // Selection defines the side parent, but the compact dock can still read a
  // different resource (including with no main selection or a main draft).
  // Every pane's list writes must yield to all of these resource owners.
  const ownedCardIds = useMemo(() => [parentId, dockOwnerId, sideOwnerMatches ? childId : null]
    .filter((id): id is string => id !== null), [parentId, dockOwnerId, sideOwnerMatches, childId]);
  // The companion is a sibling of the main surface. Its presentation follows
  // its own desktop/parent/terminal host, independently of the main subview.
  const companionVisible = !compact;
  const terminalVisible = companionVisible && options.terminal?.workbench.open === true;
  const sideAvailable = sideOwnerMatches && !compact;
  const sidePresented = sideAvailable && !terminalVisible;
  const side = useConversationPane(transport, unauthorized, source, sideTarget,
    { ...options, embedded: undefined, reportComposer: undefined, ownedCardIds, onTerminal: options.terminal?.workbench.reveal, inline: true, slotId: sideSlot, enabled: sideAvailable, presented: sidePresented });
  const main = useConversationPane(transport, unauthorized, source, mainTarget,
    { ...options, reportComposer: mainReportComposer, ownedCardIds, sideError, stacked: true, showSideCommand: !compact, retainCompanion: options.terminal !== undefined, onTerminal: options.terminal?.workbench.reveal,
      companionVisible: terminalVisible || (sidePresented && side.isOpen),
      companion: options.terminal !== undefined
        ? (group) => <TerminalCompanion workbench={options.terminal!.workbench} visible={companionVisible}
            sideOpen={sideAvailable && side.isOpen} side={side.drawerFor(group)} terminal={options.terminal!.render()} />
        : sideAvailable && side.isOpen ? side.drawerFor : undefined,
      onSide: (parent, entries, question) => {
        if (compact) { setSideError('Side conversations are available on desktop.'); return false; }
        if (capabilities.data?.conversationSide !== true) {
          setSideError('Side conversations require a server that reports support. Update or reconnect, then try again.');
          return false;
        }
        setSideError(null);
        options.terminal?.workbench.suspend();
        if (sideDraft !== null && sideDraft.submitted !== null) {
          sideTarget[1]({ kind: 'draft' });
          if (question !== '') { setSideError('This side draft still has an unsettled delivery. Continue or retry it before starting another.'); return false; }
          return;
        }
        if (question === '') {
          const saved = source.rows.find((row) => row.sourceCardId === parent.id);
          if (saved !== undefined) { sideTarget[1]({ kind: 'row', id: saved.id }); return; }
        }
        registry.startDraft({ scopeId: sideSlot, key: mintIdempotencyKey(),
          provider: parent.provider, side: sideConversationSnapshot(parent.id, entries), model: FOLLOW_INSTALLATION_DEFAULT,
          text: question === '' ? null : question, autoSend: question !== '',
          submitted: null, creating: false, error: null, remedy: null });
        sideTarget[1]({ kind: 'draft' });
      },
    });
  return main;
}

function useConversationPane(
  transport: ApiTransportPort,
  unauthorized: UnauthorizedChannel,
  source: ConversationPanelSource,
  target: ReturnType<typeof useConversationPaneTarget>,
  options?: { onPresent?: () => void; embedded?: EmbeddedConversationPresentation; onArchive?: (cardId: string, archived: boolean) => Promise<void>; onTerminal?: () => void; retainCompanion?: boolean; companionVisible?: boolean; reportComposer?: Readonly<{ ready: boolean; returnLabel: string; conversationId: string | null }>; showSideCommand?: boolean; stacked?: boolean; sideError?: string | null; ownedCardIds?: readonly string[]; showTrack?: boolean; resizable?: boolean; inline?: boolean; slotId?: string; enabled?: boolean; presented?: boolean;
    workspace?: ConversationPanelOptions['workspace'];
    companion?: (group: PaneResizeGroup) => React.ReactNode; onSide?: (source: Conversation, entries: readonly TranscriptEntry[], question: string) => void | boolean },
) {
  /* Existing conversation selection survives navigation; unfinished drafts
     retain their separate ConversationProvider lifecycle. */
  const [openTarget, setOpenTarget] = target;
  const registry = useConversationCommands();
  const sourceScopeId = options?.slotId ?? source.scopeId;
  // Terminal intent belongs to this pane's scope, including its draft. Keep the
  // event identity stable while dispatching to the latest committed Track view.
  const revealTerminal = useCommittedCallback(sourceScopeId, () => { options?.onTerminal?.(); });
  const onTerminal = options?.onTerminal === undefined ? undefined : revealTerminal;
  /* The conversation whose composer this route was asked to put the caret in. Held
       here because the request is cleared in the same commit that opens the row;
       dropped when the drawer closes. */
  const [composerFocusFor, setComposerFocusFor] = useState<string | null>(null);
  const openRowId = options?.enabled === false ? null : openTarget?.kind === 'row' ? openTarget.id : null;
  // A retained controller may keep reading resources while its pane is hidden.
  // App composition owns presentation; visible-only effects consume it here.
  const panePresented = options?.presented !== false;
  const compactViewport = useCompactViewport();
  const compactComposer = options?.reportComposer !== undefined && compactViewport && options?.enabled !== false && options?.inline !== true;
  const receipts = useReadReceipts();
  // Keep the selected conversation and its draft together when its sheet closes.
  // Status priority chooses a destination only when there is no retained owner.
  const dockRowId = compactComposer ? options?.reportComposer?.conversationId ?? null : null;
  const composerRowId = openRowId ?? dockRowId;
  const managed = useManagedProviders(transport, unauthorized, openTarget?.kind === 'draft');
  // The app owns provider metadata reads (permission modes and capabilities).
  const managedProvidersQuery = useQuery({ ...managedProvidersQueryOptions(transport, unauthorized), enabled: composerRowId !== null });
  const draftCapabilities = managed.version;
  const supportsDraftModel = draftCapabilities.data?.conversationCreateModel === true;
  useEffect(() => { if (openRowId === null) setComposerFocusFor(null); }, [openRowId]);
  const scope: PlannerConversationScope | null = composerRowId !== null
    ? source.scopeOf(composerRowId)
    : null;
  // Shared cache with the Track route, also available for Today and side conversations.
  const imageTrackId = scope?.id ?? null;
  const imageTrack = useQuery({
    ...trackDetailQueryOptions(transport, imageTrackId ?? '', unauthorized),
    enabled: imageTrackId !== null && source.workspaceRoot === null,
  });
  const imageRoot = source.workspaceRoot
    ?? (imageTrack.data === undefined ? null : toTrack(trackDtoToWire(imageTrack.data.track)).agentCwd);
  const shareAccess = useShareScope();
  const conversationFiles = useMemo(() => imageTrackId === null ? null
    : createTrackWorkspaceFilesPort(transport, unauthorized, imageTrackId, shareAccess?.shareLinkId), [imageTrackId, transport, unauthorized, shareAccess?.shareLinkId]);
  const imageFiles = useMemo(() => imageRoot === null || conversationFiles === null ? null : ({
    root: imageRoot, files: conversationFiles,
  }), [imageRoot, conversationFiles]);
  const routeIntent: ConversationRouteIntent = {
    rows: source.rows, rememberOn: source.rememberOn, ownedCardIds: options?.ownedCardIds, visible: panePresented && openRowId !== null,
  };


  const rows = source.rows;
  const store = useConversationStore(transport, unauthorized, scope, routeIntent);
  /* `@` and the Planner's questions only in the Planner's own row; the track the drawer is on ranks its blocks first. */
  const plannerRow = source.planner !== null && openRowId === source.planner.cardId ? source.planner : null;
  const mentionTrigger = useMentionTrigger(useMentionSearch(transport, unauthorized,
    plannerRow === null ? null : plannerRow.areaId, source.scopeId, undefined, source.pergaMode ?? false));
  /* The open conversation's own composer: words and images live in the registry per conversation,
       so closing keeps them and switching shows the other conversation's own. */
  const composerId = scope?.cardId ?? null;
  const heldAttachments = useConversationAttachments(composerId);
  const { editComposer } = registry;
  const setComposerText = useCallback<Dispatch<SetStateAction<string>>>((action) => {
    if (composerId === null) return;
    editComposer(composerId, (current) => {
      const text = typeof action === 'function' ? action(current.text) : action;
      return text === current.text ? current : { ...current, text };
    });
  }, [composerId, editComposer]);
  const { editUpload } = registry;
  const uploadState = useConversationUpload(composerId);
  const attachmentStore = useMemo<AttachmentStore>(() => ({
    items: heldAttachments,
    update: (cardId, next) => editComposer(cardId, (current) => {
      const attachments = next(current.attachments);
      return attachments === current.attachments ? current : { ...current, attachments };
    }),
    upload: uploadState,
    editUpload,
  }), [heldAttachments, editComposer, editUpload, uploadState]);
  const attachments = usePlannerAttachments(store.uploadAttachment, composerId ?? '', attachmentStore);
  const [composerFocusRequest, setComposerFocusRequest] = useState(0);
  const requestComposerFocus = useCallback((id: string) => {
    setComposerFocusFor(id);
    setComposerFocusRequest(count => count + 1);
  }, [setComposerFocusFor, setComposerFocusRequest]);
  const shownComposer = useRef(composerId);
  shownComposer.current = composerId;
  const focusComposer = useCommittedCallback(sourceScopeId, (conversationId: string) => {
    if (panePresented && shownComposer.current === conversationId) setComposerFocusRequest((count) => count + 1);
  });
  /* The one readiness every response action and the continue guidance share. */
  const archived = rows.find(row => row.id === composerRowId)?.archivedAt != null;
  const canContinue = !archived && store.runReady && store.runError === null && store.historyReady && !store.sendBlocked && !store.working && !store.stopping;
  /* A conversation's provider is fixed for its life; its model picker offers that provider's group alone. */
  const scopeProvider: AgentProvider = scope === null ? 'codex' : scope.provider;
  const go = useGo();
  const { openFile: openChatWorkspaceFile } = useTrackFileNavigation();
  const openChatLink = useCallback((target: ReportLinkTarget) => {
    go({ name: 'track', trackId: target.trackId, blockId: target.blockId ?? undefined });
  }, [go]);
  const openChatFile = useCallback((owner: string, target: ReportFileLinkTarget) => {
    openChatWorkspaceFile(owner, target.path);
  }, [openChatWorkspaceFile]);
  const chatLinks = useConversationLinks({ transport, unauthorized, conversationId: openRowId, trackId: imageTrackId,
    root: imageRoot, files: conversationFiles, onOpenLink: openChatLink, onOpenFile: openChatFile });
  const open = store.conversations.find((conversation) => conversation.id === openRowId) ?? null;
  const openTurns = open === null ? [] : store.turns;
  const hasPendingMessages = store.pendingQueue.length > 0 || store.pendingSends.length > 0;
  const threadAvailable = open !== null
    && (store.historyReady || openTurns.length > 0 || hasPendingMessages || store.stopFeedback !== null);
  const threadPresented = useContentAdmission(open?.id ?? null, threadAvailable);
  const outgoingPreview = open === null || threadPresented ? undefined : openTurns.findLast((entry): entry is ConversationTurn & { author: 'you' | 'human' | 'unknown' } =>
    isUserConversationTurn(entry) && entry.text !== '' && (entry.attachments?.length ?? 0) === 0);
  const conversationBodyVisible = hasPendingMessages || (threadPresented ? openTurns.length > 0 : outgoingPreview !== undefined);
  const sideSourceReady = threadPresented && store.historyReady;
  /* While an Edit is held nothing else acts on the conversation; its replace, once sent, blocks as any send does. */
  const respondable = canContinue;
  const drawerResize = useConversationDrawerResize();
  // Receipts compare the row's completion time, not `updatedAt`, which also moves
  // when the reader queues a message. `null` is never unread.
  const openActivity = rows.find(row => row.id === open?.id);
  const visibleConversation = panePresented && threadPresented && store.historyReady && !store.historyLoading && store.historyError === null;
  const openCompletedAt = openActivity?.lastTurnCompletedAt ?? null;
  useViewReadReceipt('track', panePresented && openRowId !== null ? imageTrackId : null, visibleConversation, openRowId ?? undefined);
  useViewReadReceipt('conversation', openCompletedAt === null ? openActivity?.id ?? null : null, visibleConversation, openActivity?.id);
  useReadReceipt('conversation', openCompletedAt === null ? null : openActivity?.id ?? null, openCompletedAt,
    visibleConversation && receipts.ready);

  /* Only this route's slot is visible, reopenable or sendable here. */
  const draft = useConversationDraft(sourceScopeId);
  const creating = draft?.creating ?? false;
  const draftFocus = useMemo(() => ({ key: draft?.key ?? null, handle: createRef<ComposerFocusHandle>() }), [draft?.key]);
  // Rekey keeps this scope's event channel, but only the actual current attempt
  // may hand focus to its adopted row. Its UI handle stays bound to that key.
  const onAdoptDraft = useCommittedCallback(sourceScopeId, (from: ConversationDraftId, conversationId: string) => {
    if (from.scopeId !== sourceScopeId || from.key !== draftFocus.key) return;
    if (openTarget?.kind === 'draft' && panePresented && options?.enabled !== false && draftFocus.handle.current?.ownsFocus()) {
      setComposerFocusFor(conversationId);
      setComposerFocusRequest(count => count + 1);
    }
  });
  const discardUnsentDraft = registry.discardUnsentDraft;
  /* The new conversation's composer lives in the registry per Track, so closing, `+` and leaving keep its words. */
  const { editNewConversationComposer } = registry;
  const setNewConversationText = useCallback<Dispatch<SetStateAction<string>>>((action) => {
    editNewConversationComposer(sourceScopeId, (current) => typeof action === 'function' ? action(current) : action);
  }, [editNewConversationComposer, sourceScopeId]);

  useConversationDraftRetention({ discardUnsentDraft, sourceScopeId });

  const { start, startWithMessage, withDraft, sendDraft, retryDraft, sendAsNewConversation, closeDrawer } = createConversationDraftActions({
    registry, draft, creating, source, sourceScopeId, transport, supportsDraftModel,
    defaultProvider: source.defaultProvider,
    supportsInitialInput: !draftCapabilities.isError && draftCapabilities.data?.conversationInitialInput === true,
    managedProviders: managed.answers,
    recheckManagedProviders: managed.recheck,
    supportsAssistantProviders: managed.supportsAssistantProviders,
    supportsSideConversation: draftCapabilities.data?.conversationSide === true,
    openDraft: () => { options?.onPresent?.(); setComposerFocusRequest(count => count + 1); setOpenTarget({ kind: 'draft' }); }, closeView: () => setOpenTarget(null),
    onGone: () => go({ name: 'today' }),
    onAdopt: onAdoptDraft,
  });
  const startAnother = start;

  useRequestedConversationOpen({ registry, rows, setOpenTarget, requestComposerFocus, inline: options?.inline });

  useConversationEscape({ open: panePresented ? open : null, store });

  /* A draft belonging to another Track is not open here: `draft` is read only
     from this route's provider slot. */
  const contextSourceId = draft?.side?.source_card_id ?? open?.sourceCardId;
  const contextSource = source.rows.find((row) => row.id === contextSourceId);
  const draftOpen = options?.enabled !== false && openTarget?.kind === 'draft' && draft !== null;

  useConversationDraftAutoSend({ draftOpen, draft, creating: creating || managed.loading || draftCapabilities.isLoading, registry, sendDraft });
  const pendingTrackInitialSend = usePendingTrackInitialSend(
    options?.slotId === undefined && source.planner != null && open?.id === source.planner.cardId
      ? source.scopeId : null,
  );
  const dispatchedInitialSendRef = useRef<string | null>(null);
  const pendingInitialFocusRef = useRef<string | null>(null);
  useEffect(() => {
    if (pendingTrackInitialSend === undefined || open === null || open.id !== source.planner?.cardId) return;
    if (dispatchedInitialSendRef.current === source.scopeId) return;
    const sent = store.send(open.id, pendingTrackInitialSend.text, [], false, pendingTrackInitialSend.key);
    if (sent === null) return;
    dispatchedInitialSendRef.current = source.scopeId;
    pendingInitialFocusRef.current = open.id;
    void sent.then(() => {
      registry.consumeTrackInitialSend(source.scopeId);
    });
  }, [pendingTrackInitialSend, open, source.planner?.cardId, source.scopeId, store, registry]);
  useEffect(() => {
    if (open === null || pendingInitialFocusRef.current !== open.id) return;
    if (!store.sendBlocked && store.historyReady) {
      pendingInitialFocusRef.current = null;
      focusComposer(open.id);
    }
  }, [open, store.sendBlocked, store.historyReady, focusComposer]);

  const existingId = open?.id ?? null;
  const retrySend = useCommittedCallback(existingId, () => {
    if (open !== null && store.failedSend !== null && store.retrySend(store.failedSend.key)) focusComposer(open.id);
  });
  const threadReadingView = useThreadReadingView(composerId);
  const historyPrefetch = useMemo(() => ({ active: panePresented, cursor: store.earlierCursor,
    busy: store.historyLoading, failed: store.historyError !== null, load: store.loadEarlier }),
  [panePresented, store.earlierCursor, store.historyLoading, store.historyError, store.loadEarlier]);
  const newConversation = useCommittedCallback(existingId, startAnother);
  const interrupt = useCommittedCallback(existingId, store.interrupt);
  const compact = useCommittedCallback(existingId, store.compact);
  const deleteQueuedEntry = useCommittedCallback(existingId, store.deleteQueuedEntry);
  const steerQueuedEntry = useCommittedCallback(existingId, (entry: PendingQueueEntry) => {
    const steer = store.steerQueuedEntry;
    if (steer === undefined) return Promise.reject(new Error('Steering is no longer available in this view.'));
    return steer(entry);
  });
  const setModel = useCommittedCallback(existingId, store.setModel);
  const setPermissionMode = useCommittedCallback(existingId, store.setPermissionMode);
  const [submission, setSubmission] = useState<Readonly<{ conversationId: string; request: number }> | null>(null);
  /* Only a new admitted op asks the shown thread to follow. Retries retain their
     key and reading place; pending work on mount is not a fresh local press. */
  const submitMessage: typeof store.send = (...args) => {
    const sent = store.send(...args);
    if (sent !== null) setSubmission((previous) => ({ conversationId: args[0], request: (previous?.request ?? 0) + 1 }));
    return sent;
  };
  const sendText = useCommittedCallback(existingId, (text: string) => open !== null
    && (submitMessage(open.id, text, attachments.items, true) !== null));
  const regenerateMessage = useCommittedCallback(existingId, async (message: ConversationTurn) => {
    if (open !== null) await submitMessage(open.id, message.text, message.attachments ?? [], false);
  });
  const sideQuestion = useCommittedCallback(existingId, (question: string) => {
    if (open === null || options?.onSide === undefined || !sideSourceReady) return false;
    return options.onSide(open, store.turns, question);
  });
  const conversationApprovals = useMemo(
    () => shareAccess !== null ? NO_PENDING_APPROVALS : approvalsForSession(source.pendingApprovals, openRowId, store.pendingApprovals),
    [shareAccess, source.pendingApprovals, openRowId, store.pendingApprovals],
  );
  const conversationAsks: readonly OpenAsk[] = store.pendingInteractions?.flatMap(interaction => interaction.kind === 'ask' && interaction.canRespond
    ? [{ askId: interaction.id, delivery: 'wake' as const, questions: interaction.questions?.map(question => ({ title: question.question, options: question.options })) ?? [] }] : []) ?? NO_ASKS;
  /* Read field by field: the control is rebuilt each render, and the composer's memo keys on what it shows. */
  const roleValue = plannerRow?.role.value ?? null;
  const roleDisabled = plannerRow?.role.isDisabled ?? true;
  const roleDisabledReason = plannerRow?.role.disabledReason;
  const roleChange = plannerRow?.role.onChange;
  const decideApproval = useCommittedCallback(existingId, (approval: PendingApproval, optionId: string) =>
    source.decideApproval(approval, optionId));
  const answerAsk = useCommittedCallback(existingId, (askId: import('../../../../core/domain/activity.ts').AskId, answers: readonly AskAnswer[]) =>
    store.answerAsk(askId, answers));
  const attach = useCommittedCallback(existingId, attachments.attach);
  const removeAttachment = useCommittedCallback(existingId, attachments.remove);
  const { items: attachmentItems, ids: attachmentIds, busy: attachmentBusy, error: attachmentError, atCapacity } = attachments;
  const composerAttachments = useMemo(() => ({ items: attachmentItems, ids: attachmentIds, busy: attachmentBusy,
    error: attachmentError, atCapacity, attach, remove: removeAttachment }),
  [attachmentItems, attachmentIds, attachmentBusy, attachmentError, atCapacity, attach, removeAttachment]);
  const canSteer = store.steerQueuedEntry !== undefined;
  const [pendingPresentation, setPendingPresentation] = useState<Readonly<{ conversationId: string | null; visible: boolean }> | null>(null);
  const pendingPresenceChanged = useCallback((visible: boolean) => {
    if (shownComposer.current !== existingId) return;
    setPendingPresentation(previous => previous?.conversationId === existingId && previous.visible === visible
      ? previous : { conversationId: existingId, visible });
  }, [existingId]);
  const pendingMessages = useMemo(() => <PendingQueue key={existingId}
    entries={store.pendingQueue} pendingInputCount={0} overflow={0} sending={store.pendingSends} queueDisplayAccounting={store.queueDisplayAccounting}
    busy={!threadPresented || store.sending || store.queueWriteOut || store.readonlyReason !== null} onDelete={deleteQueuedEntry}
    onPresenceChange={pendingPresenceChanged} onSteer={canSteer ? steerQueuedEntry : undefined} />,
  [existingId, threadPresented, store.pendingQueue, store.pendingSends, store.queueDisplayAccounting, store.sending, store.queueWriteOut, store.readonlyReason,
    deleteQueuedEntry, pendingPresenceChanged, canSteer, steerQueuedEntry]);
  const scopePermissionModes = useMemo(
    () => permissionModesForProvider(managedProvidersQuery.data, scopeProvider),
    [managedProvidersQuery.data, scopeProvider],
  );
  const hasSideConversation = options?.onSide !== undefined;
  const composerView = useMemo(() => ({
    compact: compact,
    compacting: store.compacting,
    manualCompactSupported: store.manualCompactSupported,
    attachmentsSupported: false,
    contextUsage: store.contextUsage,
    historyReady: store.historyReady,
    runReady: store.runReady,
    interrupt: interrupt,
    model: store.model,
    modelCatalog: store.modelCatalog,
    modelDefaultAvailable: store.modelDefaultAvailable,
    modelChangeDisabled: store.modelChangeDisabled,
    permissionChangeDisabled: store.permissionChangeDisabled,
    permissionMode: store.permissionMode,
    sendBlocked: store.sendBlocked,
    composerBlocked: store.composerBlocked,
    sending: store.sending,
    canQueueWhileRunning: store.canQueueWhileRunning,
    setModel: setModel,
    setPermissionMode: setPermissionMode,
    stopping: store.stopping,
    working: store.working
  }), [compact, store.compacting, store.manualCompactSupported, store.contextUsage, store.historyReady, store.runReady, interrupt, store.model, store.modelCatalog, store.modelDefaultAvailable, store.modelChangeDisabled, store.permissionChangeDisabled,
    store.permissionMode, store.sendBlocked, store.composerBlocked, store.sending, store.canQueueWhileRunning, setModel, setPermissionMode, store.stopping, store.working]);
  const permissionOptions = store.permissionOptions ?? scopePermissionModes;
  const permissionControl = useMemo(() => resolvePermissionControl({
    storedMode: composerView.permissionMode,
    providerModes: permissionOptions,
    metadataLoaded: store.permissionOptions !== null || managedProvidersQuery.data !== undefined,
    isDisabled: !composerView.historyReady || composerView.permissionChangeDisabled,
  }), [composerView.permissionMode, permissionOptions, store.permissionOptions, managedProvidersQuery.data,
    composerView.historyReady, composerView.permissionChangeDisabled]);
  const replacing = false;
  const editingQueue = false;
  const changeComposer = setComposerText;
  const composerStop = store.canStop ? composerView.interrupt : undefined;
  const plannerCommands = plannerRow?.commands;
  const switchPlanner = usePlannerSwitchControl(transport, unauthorized, scope);
  const plannerSwitchBinding = useMemo(() => scope !== null && scope.isLead !== true
    ? { execute: switchPlanner } : undefined, [scope, switchPlanner]);
  const recover = useCommittedCallback(existingId, (mode: 'reconnect' | 'reset_context') => store.recover(mode));
  const commandEnabled = useCallback((command: 'stop' | 'compact' | 'reconnect' | 'reset') =>
    conversationCommandEnabled(command, store.sessionState, store.readonlyReason, store.unknownDetail, store.recovering, {
      canStop: store.canStop, manualCompactSupported: store.manualCompactSupported,
      canResetContext: true,
    }), [store.sessionState, store.readonlyReason, store.unknownDetail, store.recovering, store.canStop,
      store.manualCompactSupported]);
  const paneCommands = useMemo(() => composerCommands({
    ...conversationCommandBindings({
      newConversation: options?.inline === true ? undefined : newConversation,
      compact: composerView.manualCompactSupported && composerView.historyReady ? composerView.compact : undefined,
      side: !hasSideConversation || !composerView.historyReady ? undefined : sideQuestion,
      sideHidden: options?.showSideCommand === false,
      sideUnavailableReason: sideSourceReady ? undefined : 'Side conversations are not available here yet.',
      terminal: onTerminal, compactViewport: compactComposer,
    }),
    ...plannerCommands?.(composerStop),
    planner: plannerSwitchBinding,
    ...Object.fromEntries((['stop', 'compact', 'reconnect', 'reset'] as const).map(command => [command, {
      execute: () => { if (!commandEnabled(command)) return;
        if (command === 'stop') interrupt(); else if (command === 'compact') compact();
        else recover(command === 'reset' ? 'reset_context' : 'reconnect'); },
      unavailableReason: commandEnabled(command)
        ? undefined : '当前会话状态下不可用',
    }])),
  }), [options?.inline, options?.showSideCommand, newConversation, composerView.manualCompactSupported, composerView.historyReady,
    composerView.compact, hasSideConversation, sideQuestion, sideSourceReady, onTerminal, compactComposer, plannerCommands, composerStop, plannerSwitchBinding, interrupt, compact, recover, commandEnabled]);
  const draftOnly = options?.workspace?.waiting === true;
  const composerNode = useMemo(() => existingId === null ? null : (
            <ConversationComposer target={{ kind: 'card', id: existingId }} layout={compactComposer ? 'mobile' : 'standard'}
              /* Read at mount only, which is what makes it one-shot; the flag is dropped
                               when the drawer closes. */
              focusOnMount={panePresented && composerFocusFor === existingId}
              focusRequest={composerFocusRequest}
              changeText={changeComposer}
              disabled={(!draftOnly && composerView.composerBlocked) || !composerView.historyReady || !composerView.runReady || replacing}
              sendDisabled={draftOnly || composerView.sendBlocked}
              sendWaiting={composerView.sending}
              /* The images stay with the composer until the store reports them delivered; the press waits out its Edit. */
              commands={paneCommands}
              onSend={sendText}
              canQueueWhileRunning={composerView.canQueueWhileRunning}
              allowEmptyText={!editingQueue && composerAttachments.items.length > 0}
              onFiles={editingQueue || !composerView.attachmentsSupported || composerAttachments.busy || composerAttachments.atCapacity ? undefined : (files) => {
                for (const file of files) void composerAttachments.attach(file);
              }}
              /* Questions and attachments belong to the composer; waiting messages follow the running status. */
              drawer={(
                <>
                  <PlannerAttachmentDrawer attachments={composerAttachments} />
                </>
              )}
              /* Renders nothing until the harness has reported a usage frame. */
              sendAdornment={compactComposer ? undefined : <ContextRing usage={composerView.contextUsage} />}
              /* `stopping` keeps Stop shown while the interrupt is in flight; `interrupt()`
                               already refuses a second one. */
              onStop={composerStop}
              mentionTrigger={mentionTrigger}
              /* The kernel reads the selection when it hands a batch to codex, so a change
                               lands on the next turn not yet issued; a REFUSED turn is re-issued and
                               reads it again. */
              /* With `headerActions` unset Astryx does not render that row at all. */
              footerActions={(
                /* The same controls can live in a desktop sidebar or the phone's options panel. */
                <HStack gap={1} align="center" wrap="wrap" className={footerStyles.group}>
                  <PlannerAttachButton
                    attachments={composerAttachments}
                    support={{
                      available: composerView.attachmentsSupported,
                      reason: ATTACHED_WORKSPACE_REASON,
                    }}
                    disabled={editingQueue || composerView.sendBlocked || !composerView.historyReady || replacing}
                  />
                  <ModelPill key={`model-${existingId}`}
                    groups={[{ provider: scopeProvider, catalog: composerView.modelCatalog, availability: null,
                      defaultAvailable: composerView.modelDefaultAvailable }]}
                    provider={scopeProvider}
                    selection={composerView.model}
                    onChange={composerView.setModel}
                    isDisabled={!composerView.historyReady || composerView.modelChangeDisabled}
                    appliesNextTurn={composerView.working}
                  />
                  {permissionControl.visible && (
                    <ConversationApprovalControl key={`approvals-${existingId}`}
                      mode={permissionControl.mode}
                      options={permissionControl.options}
                      change={composerView.setPermissionMode}
                      disabled={permissionControl.isDisabled}
                    />
                  )}
                </HStack>
              )}
            />
  ), [existingId, compactComposer, panePresented, composerFocusFor, composerFocusRequest, changeComposer, composerView, draftOnly,
    paneCommands, composerStop, replacing, editingQueue,
    sendText, composerAttachments, mentionTrigger, scopeProvider, permissionControl]);

  const [archiveFailure, setArchiveFailure] = useState<Readonly<{ id: string; message: string }> | null>(null);
  const archiveLease = useRef(new Set<string>());
  const archiveError = archiveFailure?.id === existingId ? archiveFailure.message : null;
  const changeArchive = (archived: boolean) => {
    if (open === null || options?.onArchive === undefined || archiveLease.current.has(open.id)) return;
    const id = open.id;
    archiveLease.current.add(id);
    setArchiveFailure(null);
    void options.onArchive(id, archived).catch((error: unknown) => {
      if (shownComposer.current === id) setArchiveFailure({ id, message: error instanceof Error ? error.message : '无法更新归档状态，请重试。' });
    }).finally(() => { archiveLease.current.delete(id); });
  };
  const action = deriveConversationAction({
    readError: store.historyError ?? store.runError,
    readonlyReason: store.readonlyReason, canReopen: store.canReopen,
    state: store.sessionState, unknownDetail: store.unknownDetail,
    hasAsks: conversationAsks.length > 0 || conversationApprovals.length > 0,
    localSend: store.failedSend !== null ? 'failed' : store.sending ? 'sending' : 'idle',
    sendUnconfirmed: store.failedSend?.delivery === 'unknown',
    actionError: store.actionError ?? archiveError ?? options?.sideError ?? null, workerPaused: store.workerPaused,
  });
  const [pausedDismissedFor, setPausedDismissedFor] = useState<string | null>(null);
  useEffect(() => { if (!store.workerPaused) setPausedDismissedFor(null); }, [store.workerPaused]);
  const actionArea = action === null || (action.kind === 'workerPaused' && pausedDismissedFor === existingId)
    ? options?.workspace?.notice == null ? null : <section data-nc-conversation-action="workspace" aria-label="工作区状态">{options.workspace.notice}</section> :
    <section data-nc-conversation-action={action.kind} aria-label="待处理操作">
      {action.kind === 'ask' ? <><ApprovalDrawer approvals={conversationApprovals} onDecide={decideApproval} />
        <PlannerAskDrawer asks={conversationAsks} onAnswer={answerAsk} /></> :
      <ChatFooterNotice tone={action.kind === 'readonly' || action.kind === 'workerPaused' ? 'neutral' : 'error'}>
        {action.kind === 'readError' ? <>
          <span>无法同步最新对话状态</span><span>{action.message}</span><span>刷新仅重新读取数据。</span>
          <ChatFooterRemedy pending={store.historyLoading || store.runLoading} onClick={() => { if (store.historyError !== null) store.retryHistory(); if (store.runError !== null) store.retryRun(); }}>刷新</ChatFooterRemedy>
        </> : action.kind === 'readonly' ? <>
          <span>{action.reason === 'archived' ? '对话已归档' : action.reason === 'track_closed' ? 'Track 已关闭' : '对话已结束'}</span>
          {archiveError !== null && <ChatFooterError message={archiveError} />}
          {action.canReopen && options?.onArchive !== undefined && <ChatFooterRemedy onClick={() => changeArchive(false)}>恢复对话</ChatFooterRemedy>}
        </> : action.kind === 'unknown' ? <>
          <span>会话状态未知</span><span>{action.detail?.detail}</span>
          {action.detail?.lastError && <ChatFooterError message={action.detail.lastError} />}
          {store.actionError !== null && <ChatFooterError message={store.actionError} />}
          {action.detail?.canReconnect && <ChatFooterRemedy disabled={store.recovering} onClick={() => store.recover('reconnect')}>重连原会话</ChatFooterRemedy>}
          {action.detail?.canResetContext && <ChatFooterRemedy disabled={store.recovering} onClick={() => store.recover('reset_context')}>重置上下文并继续</ChatFooterRemedy>}
          {store.recovering && <span>正在恢复会话…</span>}
        </> : action.kind === 'sendFailed' ? <>
          <span>{action.unconfirmed ? '发送结果未确认。' : `消息未发送。${store.failedSend?.code === 'session_recovery_required' ? '会话需要恢复，请先恢复会话后再发送。' : store.failedSend?.message ?? ''}`}</span>
          <ChatFooterRemedy disabled={store.sending} onClick={retrySend}>{action.unconfirmed ? '安全重试（同幂等键）' : '重试'}</ChatFooterRemedy>
          <ChatFooterRemedy onClick={() => { if (store.failedSend !== null) {
            if (action.unconfirmed) store.dismissFailedSend(store.failedSend.key);
            else { store.discardFailedSend(store.failedSend.key); if (existingId !== null) focusComposer(existingId); }
          } }}>{action.unconfirmed ? '忽略' : '放回输入框'}</ChatFooterRemedy>
        </> : <>
          <span>{action.kind === 'actionError' ? action.message : '已暂停，发送消息即可继续。'}</span>
          <ChatFooterRemedy onClick={() => { if (action.kind === 'workerPaused') setPausedDismissedFor(existingId); else { store.dismissActionError(); setArchiveFailure(null); } }}>关闭</ChatFooterRemedy>
        </>}
      </ChatFooterNotice>}
    </section>;

  const centeredEmpty = options?.embedded?.active === true && open !== null && store.historyReady && openTurns.length === 0 && !hasPendingMessages && !(pendingPresentation?.conversationId === existingId && pendingPresentation.visible) && !store.sending && !store.working && store.failedSend === null;

  const renderDrawer = (resizeGroup: PaneResizeGroup | null = null) => (
      <ConversationSurface
        embedded={options?.embedded}
        centerEmptyComposer={centeredEmpty}
        mobileSheet={compactComposer}
        focusInput={panePresented && (draftOpen || (open !== null && composerFocusFor === open.id))}
        contextTitle={scope?.title}
        sessionHeader={open === null ? undefined : <ConversationSessionHeader session={store.session} state={store.sessionState}
          readonlyReason={store.readonlyReason} workerPaused={store.workerPaused}
          compactEnabled={commandEnabled('compact') && store.manualCompactSupported}
          resetEnabled={commandEnabled('reset')}
          onCompact={compact} onReset={() => store.recover('reset_context')}
          onArchive={store.session?.isLead === true || store.session?.boundTaskId != null || options?.onArchive === undefined || (store.readonlyReason !== null && !store.canReopen) ? undefined : () => changeArchive(store.readonlyReason !== 'archived')} />}
        resizeGroup={resizeGroup}
        id={open === null ? undefined : `conversation-${open.id}`}
        inline={options?.inline}
        stacked={options?.stacked}
        companion={options?.companion}
        companionVisible={options?.companionVisible}
        retainCompanion={options?.retainCompanion}
        closeLabel={options?.inline === true ? 'Close side conversation' : 'Close conversation'}
        open={open !== null || draftOpen}
        /* A draft has no name yet, and naming it after the words being typed
           would rename the drawer on every keystroke. */
        title={options?.inline === true ? 'Side conversation · Codex' : open !== null ? conversationName(open) : draftOpen ? 'Untitled' : ''}
        mobileBackLabel={options?.reportComposer?.returnLabel ?? 'Conversations'}
        onClose={closeDrawer}
        resize={options?.resizable === false ? undefined : drawerResize}
        footer={draftOpen ? (
          <>
            {/* The strip is welded to the well's top edge, so it renders before the composer. */}
            {draft != null && (draft.error != null || draft.remedy !== null) && (
              <ChatFooterNotice>
                {draft.error != null && <ChatFooterError message={draft.error} />}
                {draft.remedy === 'retry' && (
                  <ChatFooterRemedy disabled={creating} onClick={() => {
                    draftFocus.handle.current?.focus();
                    if (retryDraft()) setComposerFocusRequest(count => count + 1);
                  }}>Try again</ChatFooterRemedy>
                )}
                {draft.remedy === 'new-conversation' && (
                  <ChatFooterRemedy disabled={creating} onClick={() => { if (sendAsNewConversation()) setComposerFocusRequest(count => count + 1); }}>
                    Send as a new conversation
                  </ChatFooterRemedy>
                )}
              </ChatFooterNotice>
            )}
            {/* Initial submit hands words to the held draft, including refusal; only recovery uses the takeover boolean. */}
            <ConversationComposer commands={conversationCommands({ terminal: onTerminal, compactViewport: compactComposer, newConversation: options?.inline === true ? undefined : startAnother })} target={{ kind: 'scope', id: sourceScopeId }} layout={compactComposer ? 'mobile' : 'standard'} disabled={creating} sendWaiting={creating} onSend={text => { sendDraft(text); }}
              focusOnMount={panePresented} focusRequest={composerFocusRequest} focusHandle={draftFocus.handle}
              mentionTrigger={mentionTrigger}
              changeText={setNewConversationText}
              /* Menu membership and readiness come from the managed backend declarations. */
              footerActions={(() => {
                const draftPermissionControl = resolvePermissionControl({
                  storedMode: draft?.permissionMode,
                  providerModes: draft === null ? [] : permissionModesForProvider(managed.answers, draft.provider),
                  metadataLoaded: managed.answers !== undefined,
                  isDisabled: creating || draft.submitted !== null,
                });
                return (
                  <HStack gap={1} align="center" wrap="wrap" className={footerStyles.group}>
                    <PlannerAttachButton
                      attachments={composerAttachments}
                      support={{ available: false, reason: ATTACHED_WORKSPACE_REASON }}
                      disabled
                    />
                    <ModelPill groups={managed.groups} provider={draft.provider} selection={draft.model}
                      effortControl={compactComposer ? 'in-menu' : 'separate'}
                      onChange={(model, provider) => withDraft(draft, current => {
                        if (current.creating || current.submitted !== null) return current;
                        return {
                          ...current,
                          model,
                          provider,
                          permissionMode: permissionModeAfterProviderChange(current.provider, provider, current.permissionMode),
                        };
                      })}
                      isDisabled={creating || draft.submitted !== null || !supportsDraftModel || !managed.supportsAssistantProviders} />
                    {draftPermissionControl.visible && (
                      <PermissionModePill
                        mode={draftPermissionControl.mode}
                        options={draftPermissionControl.options}
                        onChange={(permissionMode) => withDraft(draft, current =>
                          current.creating || current.submitted !== null ? current : { ...current, permissionMode })}
                        isDisabled={draftPermissionControl.isDisabled}
                      />
                    )}
                    {roleValue !== null && roleChange !== undefined && (
                      <PlannerRolePill value={roleValue} onChange={roleChange} isDisabled={roleDisabled}
                        disabledReason={roleDisabledReason} />
                    )}
                  </HStack>
                );
              })()} />
          </>
        ) : open === null ? undefined : (
          <>
            {actionArea}
            {store.readonlyReason === null && composerNode}
          </>
        )}
      >
        {open !== null && !draftOpen && !archived && roleValue !== null && roleChange !== undefined && (
          <PlannerRolePill value={roleValue} onChange={roleChange} isDisabled={roleDisabled} placement="below"
            disabledReason={roleDisabledReason} />
        )}
        {(draft?.side !== undefined && draftOpen || open?.sourceCardId !== undefined) && (
          <p>From {contextSource === undefined ? 'an earlier conversation' : conversationName(contextSource)}.
            {' '}Uses a snapshot of loaded text, not complete history. Choose the backend before sending.</p>
        )}
        {/* Shown back because the composer clears its field on send; nothing here
                    claims they arrived. */}
        {draftOpen && (draft?.text == null
          ? <ChatEmpty />
          : <>
            <OutgoingMessage speaker="you" text={draft.text} mobile={compactViewport} />
            {creating && <p role="status">Sending…</p>}
          </>)}
        {open !== null && (
          <>
            {!conversationBodyVisible && (!threadPresented || !store.historyReady) && store.historyError === null && (
              <p role="status">Loading conversation…</p>
            )}
            {/* Keyed on the conversation: the drawer stays mounted across a switch, and
                          without the key `ChatThread`'s follow-the-newest refs and rail state
                          would be A's applied to B. */}
            {/* Not while the first page is unknown and there is nothing to show:
                          `ChatThread` draws its empty state (with the live dot) for an empty list.
                          `turnsOf` is the second arm so a reopen whose query was collected still
                          shows the remembered transcript. */}
            {!threadPresented && outgoingPreview !== undefined && <OutgoingMessage speaker={outgoingPreview.author} text={outgoingPreview.text} mobile={compactViewport} />}
            {!threadPresented && hasPendingMessages && pendingMessages}
            {threadPresented && !centeredEmpty && (
              <ChatThread historyPrefetch={historyPrefetch} readingView={threadReadingView} key={open.id} pendingMessages={pendingMessages}
                hasPendingMessages={hasPendingMessages || (pendingPresentation?.conversationId === existingId && pendingPresentation.visible)}
                conversation={open}
                imageFiles={imageFiles}
                renderLink={chatLinks.renderLink}
                turns={openTurns} lastSessionTurn={store.lastTurn}
                sending={store.sending}
                submissionRequest={submission?.conversationId === open.id ? submission.request : 0}
                pending={store.working}
                cards={source.cards}
                stalled={(store.sessionState === 'unknown')}
                statusLoading={store.runLoading}
                copyText={writeClipboardText}
                regenerateMessage={respondable
                  ? regenerateMessage
                  : undefined}
                editMessage={undefined}
                canContinue={canContinue}
              />
            )}
          </>
        )}
        {chatLinks.sourceDrawer}
      </ConversationSurface>
  );

  const openConversation = useCommittedCallback(sourceScopeId, (conversation: Conversation) => {
    options?.onPresent?.();
    requestComposerFocus(conversation.id);
    setOpenTarget({ kind: 'row', id: conversation.id });
  });
  const unreadIds = useMemo(() => new Set(rows.filter(row => receipts.isUnread('conversation', row.id,
    row.lastTurnCompletedAt ?? null)).map(row => row.id)), [rows, receipts]);
  const listProps = useMemo(() => ({
    conversations: store.conversations, cards: source.cards, unreadIds, activeId: existingId,
    showTrack: options?.showTrack ?? true, onOpen: openConversation, onArchive: options?.onArchive,
  }), [store.conversations, source.cards, unreadIds, existingId, options?.showTrack, options?.onArchive, openConversation]);
  const listNode = useMemo(() => <ChatList {...listProps} />, [listProps]);
  const contextList = useMemo(() => <ChatList {...listProps} context />, [listProps]);

  const dockViewId = JSON.stringify([sourceScopeId, dockRowId]);
  const openDockHistory = useCommittedCallback(dockViewId, () => {
    if (dockRowId !== null) {
      requestComposerFocus(dockRowId);
      setOpenTarget({ kind: 'row', id: dockRowId });
    } else start();
  });
  const setDockText = useCommittedCallback(dockViewId, (text: SetStateAction<string>) => {
    if (dockRowId === null) setNewConversationText(text);
    else setComposerText(text);
  });
  const sendFromDock = useCommittedCallback(dockViewId, (text: string) => {
    if (dockRowId !== null) {
      if (submitMessage(dockRowId, text, registry.composerOf(dockRowId).attachments, true) !== null) {
        setOpenTarget({ kind: 'row', id: dockRowId });
      }
    } else if (startWithMessage(text)) setNewConversationText('');
  });

  return {
    dock: compactComposer ? <ConversationDock target={dockRowId === null ? { kind: 'scope', id: sourceScopeId } : { kind: 'card', id: dockRowId }}
      commands={dockRowId === null ? conversationCommands({ compactViewport: true }) : paneCommands}
      onChange={setDockText} onSend={sendFromDock} onBeginInput={openDockHistory}
      disabled={archived || options?.reportComposer?.ready === false || (dockRowId !== null && (store.sendBlocked || !store.historyReady || !store.runReady))} /> : undefined,
    selectedConversationId: draftOpen ? null : open?.id ?? dockRowId,
    isOpen: open !== null || draftOpen,
    close: closeDrawer,
    list: listNode,
    contextList,
    openConversation,
    startConversation: start,
    drawer: renderDrawer(),
    drawerFor: renderDrawer,
  };
}

function TrackRoute({ transport, unauthorized, cardRuntime, recentFiles, prepareReport, pergaMode = false }: {
  prepareReport: ReportPreparationPort;
  transport: ApiTransportPort;
  unauthorized: UnauthorizedChannel;
  cardRuntime: CardRuntime;
  recentFiles: RecentFileHistory;
  pergaMode?: boolean;
}) {
  const trackId = useRouteParam('/track/');
  const registry = useConversationCommands();
  const openRequest = useConversationOpenRequest();
  const detail = useQuery({
    ...trackDetailQueryOptions(transport, trackId ?? '', unauthorized),
    enabled: trackId !== undefined,
    refetchInterval: (query) => query.state.data?.workspacePreparation === 'pending' && query.state.data.track.closedAtMs == null ? 1000 : false,
  });
  const retry = useMutation({
    mutationFn: (id: string) => runOperation(transport, retryTrackWorkspaceOperation(id), unauthorized),
    onSettled: () => detail.refetch(),
  });
  /* One `Track` per detail read, not per render: the body memoises on it. */
  const detailData = detail.data;
  const track = useMemo(
    () => detailData === undefined ? null : toTrack(trackDtoToWire(detailData.track), trackActivityFromSnapshot(detailData)),
    [detailData],
  );
  const requestedCard = detail.data?.sessions.find(session => session.id === openRequest?.id);
  const detailMatchesRoute = trackId !== undefined && detail.data?.track.id === trackId;
  useEffect(() => {
    if (openRequest === null || detail.isLoading || detail.isFetching) return;
    if (!detailMatchesRoute || requestedCard === undefined) registry.clearOpenRequest();
  }, [detail.isFetching, detail.isLoading, detailMatchesRoute, registry, requestedCard, openRequest]);

  if (!detail.data || track === null) {
    if (detail.isLoading || detail.isFetching) return <TrackPending />;
    if (detail.error instanceof Error) return <ErrorBox message={readErrorText(detail.error, 'This track could not be loaded.')} onRetry={() => { void detail.refetch(); }} />;
    return <PendingRoute label="Track" owner="features/track" missing />;
  }
  // `detail.data` can still be the previously-viewed track while this one
  // fetches; rendering it under this URL would show the wrong track.
  if (trackId !== undefined && detail.data.track.id !== trackId) return <TrackPending />;

  return (
    <TrackRouteBody
      key={track.id}
      prepareReport={prepareReport}
      transport={transport}
      unauthorized={unauthorized}
      snapshot={detail.data}
      workspaceWaiting={detail.data.workspacePreparation != null}
      workspaceNotice={detail.data.workspacePreparation === 'failed' && detail.data.track.closedAtMs != null
        ? <Banner status="error" title="Reopen this Track to retry workspace setup." />
        : detail.data.workspacePreparation === 'failed'
        ? <ErrorBox message={retry.isError
          ? readErrorText(retry.error, 'Workspace setup could not be restarted.')
          : 'Workspace setup failed. Work that needs the workspace is waiting.'}
          actionLabel="Retry workspace setup" pending={retry.isPending}
          onRetry={() => retry.mutate(track.id)} />
        : detail.data.workspacePreparation === 'unknown'
        ? <Banner status="error" title="The last workspace setup result could not be confirmed. Work that needs the workspace is waiting." />
        : detail.data.workspacePreparation === 'pending'
        ? <p role="status">Preparing the workspace.</p>
        : undefined}
      cardRuntime={cardRuntime}
      recentFiles={recentFiles}
      pergaMode={pergaMode}
    />
  );
}

function TrackRouteBody({
  transport, unauthorized, snapshot, showWorkspaceIsolation = true, cardRuntime, recentFiles, reportEvidence, reportHeader, reportEmpty, panelContent, mobileReportIntro, prepareReport, center, pergaMode = false, workspaceWaiting = false, workspaceNotice,
}: {
  transport: ApiTransportPort;
  unauthorized: UnauthorizedChannel;
  snapshot: TrackAggregateSnapshotDto;
  cardRuntime: CardRuntime;
  recentFiles: RecentFileHistory;
  center?: DailyCenterPresentation;
  showWorkspaceIsolation?: boolean;
  reportEvidence?: ReactNode;
  reportHeader?: ReactNode;
  workspaceWaiting?: boolean;
  workspaceNotice?: ReactNode;
  panelContent?: ReactNode;
  mobileReportIntro?: ReactNode;
  prepareReport: ReportPreparationPort;
  reportEmpty?: ReactNode;
  pergaMode?: boolean;
}) {
  const track = useMemo(() => toTrack(trackDtoToWire(snapshot.track), trackActivityFromSnapshot(snapshot)), [snapshot]);
  const canReopenTrack = snapshot.track.closedAtMs != null;
  const canCloseTrack = !canReopenTrack;
  const adminGrantedAt = null;
  const canAdminTrack = false;
  const toolsQuery = useQuery(trackToolsQueryOptions(transport, snapshot.track.id, unauthorized));
  const rawCards = useMemo(() => toolsQuery.data ?? [], [toolsQuery.data]);
  const [centerHost, setCenterHost] = useState<HTMLDivElement | null>(null);
  const isPergaTrack = pergaMode;
  const preferences = useUiPreferences();
  const cards = useMemo(() => {
    if (!isPergaTrack) return rawCards;
    const withPlacement = rawCards.map((card) => {
      if (card.kind !== 'terminal') return card;
      const visible = preferences.terminalWorkbench(track.id, card.id);
      if (visible === null || visible === terminalPresentation(card).in_workbench) return card;
      return { ...card, payload: {
        ...(card.payload as object),
        terminal_presentation: { ...terminalPresentation(card), in_workbench: visible },
      } };
    });
    return withPlacement.some((card, index) => card !== rawCards[index]) ? withPlacement : rawCards;
  }, [isPergaTrack, preferences, rawCards, track.id]);
  const shareAccess = useShareScope();
  const openShare = useShareManagement();
  const areasForShare = useQuery(areaListQueryOptions(transport, unauthorized));
  const shareable = !isPergaTrack && shareAccess === null && areasForShare.data?.some(area => area.id === track.areaId && area.kind === 'user') === true;
  const reportPresentation = useTrackReportPresentation(track.id, snapshot.report, prepareReport);
  const progressDisclosure = useTrackViewState(track.id, reportPresentation.presentable);
  // The same key and comparison point the rail uses: the overlay's completion
  // high-water mark, never the row's `updatedAt`.
  // A completed navigation also acknowledges a repeated selection of this already rendered Track.
  const navigationCompletedAt = useRouterState({ select: (state) => state.loadedAt });
  const trackMutations = useTrackMutations(transport, unauthorized);
  const plannerRole = usePlannerRoleControl({ trackId: track.id, adminGrantedAt, canAdmin: canAdminTrack,
    setAdmin: (admin) => trackMutations.patch(track.id, track.areaId, { admin }) });
  const closedControl = useTrackClosedControl({ trackId: track.id, canReopen: canReopenTrack, canClose: canCloseTrack,
    patch: closed => trackMutations.patch(track.id, track.areaId, { closed }) });
  const { pending: closedPending, setClosed } = closedControl;
  const plannerCommands = useCallback((stop: (() => void) | undefined) => trackCommandBindings({
    canReopen: canReopenTrack, canClose: canCloseTrack, closed: { pending: closedPending, setClosed }, stop,
  }), [canReopenTrack, canCloseTrack, closedPending, setClosed]);
  const conversationMutations = useTrackConversationMutations(transport, track.id, unauthorized);
  const openMobileSection = useOpenMobileSection();
  const mobileHeaderActionsHost = useMobileHeaderActionsHost();
  const mobileHeaderTitleHost = useMobileHeaderTitleHost();
  const go = useGo();
  const goSameTrack = useGoSameTrack();
  const fileNavigation = useTrackFileNavigation();
  const { openPanel, closePanel } = useTrackPanelNavigation();
  const requestedCardId = useRouteCardId();
  const rawRequestedFilePath = useRouteFilePath();
  const requestedFilePath = requestedCardId === null ? rawRequestedFilePath : null;
  const [recentFilePaths, setRecentFilePaths] = useState<readonly string[]>(
    () => recentFiles.read(track.id),
  );
  const fileReturnFocusRef = useRef<HTMLElement | null>(null);
  const previousFilePathRef = useRef(requestedFilePath);
  useLayoutEffect(() => {
    const previous = previousFilePathRef.current;
    previousFilePathRef.current = requestedFilePath;
    if (previous === null || requestedFilePath !== null) return;
    const opener = fileReturnFocusRef.current;
    fileReturnFocusRef.current = null;
    // The stable region exists even when report preparation has not completed.
    const target = opener?.isConnected === true
      ? opener
      : document.querySelector<HTMLElement>('section[aria-label="Track report"]');
    target?.focus({ preventScroll: true });
  }, [requestedFilePath]);
  /* The URL is turned into props here: `features-no-app` keeps `TrackPage` a pure
   * renderer. A live `?card=` wins over a hand-edited `?panel=`. */
  const routePanel = useRoutePanel();
  /* Whether `?panel=` describes anything at all on this viewport. */
  const compactViewport = useCompactViewport();
  const centerActive = center !== undefined && !compactViewport;
  /* `?from=` is a property of this visit, so every same-track move hands it back
   * explicitly (`go` clears what it is not given); crossing tracks drops it. */
  const routeFrom = useRouteFrom() ?? undefined;
  const cardRegistry = cardRuntime.registry;
  const trackCardHost = useMemo(
    () => isPergaTrack
      ? createCardHost(cardRegistry, {
          files: createTrackCardFilesPort(transport, unauthorized, track.id, track.agentCwd, shareAccess?.shareLinkId),
        })
      : cardRuntime.host,
    [isPergaTrack, cardRegistry, transport, unauthorized, track.id, track.agentCwd, shareAccess?.shareLinkId, cardRuntime.host],
  );
  // Role and conversation metadata come directly from the aggregate sessions.
  const trackReceipts = useReadReceipts();
  const plannerCard = snapshot.sessions.find(session => session.isLead);
  const registry = useConversationCommands();
  const trackTitle = trackDisplayTitle(track.title);
  /* Redeeming the planner-open intent: `armed` is already "this track, this visit",
   * and the planner card's id exists only once the detail has landed. `disarm()`
   * before the open, unconditionally: an intent left armed on this entry would
   * fire on the next visit to it (Back reaches one). */
  const plannerOpenIntent = usePlannerOpenIntent(track.id);
  useEffect(() => {
    if (!plannerOpenIntent.armed) return;
    plannerOpenIntent.disarm();
    if (plannerCard === undefined) return;
    registry.requestOpen(plannerCard.id, { focusComposer: true });
  }, [registry, plannerCard, plannerOpenIntent]);
  const rows = useMemo(() => snapshot.sessions.map(session => ({ ...agentSessionDtoToConversation(session), trackTitle }))
    .toSorted((left, right) => Number(right.isLead) - Number(left.isLead)), [snapshot.sessions, trackTitle]);
  const pendingApprovals = snapshot.pendingApprovals ?? NO_PENDING_APPROVALS;
  const openAsks = useMemo(() => openAsksOf(track.attentionItems), [track.attentionItems]);
  /* The runtime half of the TASKS panel, keyed `['track-report', trackId]`: the key
   * `invalidation-plan` refreshes on every `task.*` event and `track.report_edited`. */
  /* Read before the query: the poll's interval depends on the rows this report can produce. */
  const { report, outline } = reportPresentation;
  const reportBlocks = report?.blocks ?? null;
  const verdictsQuery = useQuery(
    trackTaskVerdictsQueryOptions(transport, track.id, unauthorized, reportBlocks),
  );
  const verdicts = verdictsQuery.data;
  const { tasks: joinedTasks } = useMemo(() => ({
    /* The verdicts land a round-trip after the declarations; `undefined` renders
           the statusless list rather than a hole. */
    tasks: deriveReportTasks(reportBlocks, verdicts),
  }), [reportBlocks, verdicts]);
  /* The CARDS module lists cards that have a surface; unclaimed kinds stay listed,
   * and `originalIndex` is bound before the filter so the merge keeps wire order. */
  const panelCards = useMemo(() => {
    const { visible, unknown } = partitionTrackCards(cardRegistry, cards);
    return [...visible, ...unknown]
      .sort((left, right) => left.originalIndex - right.originalIndex)
      .map((slot) => slot.wire);
  }, [cardRegistry, cards]);
  const tasks = useCurrentTaskRows(transport, unauthorized, track.id, joinedTasks);
  /* From the task rows the TASKS module draws, so a worker card and its row read one attempt status.
   * `tasks` is a new array every render: keyed by content, so the board's items keep their identity. */
  const noticeKey = JSON.stringify([...parkedWorkerNotices(tasks)]);
  const notices = useMemo(() => new Map(JSON.parse(noticeKey) as [string, string][]), [noticeKey]);
  const gridItems: readonly BoardHostItem[] = useMemo(() => {
    const { visible } = partitionTrackCards(cardRegistry, cards);
    return [...visible]
      .sort((left, right) => {
        const sort = left.wire.sort - right.wire.sort;
        return sort !== 0 ? sort : left.originalIndex - right.originalIndex;
      })
      .map((slot) => Object.freeze({
        card: slot.card,
        title: slot.wire.title ?? slot.wire.kind,
        originalIndex: slot.originalIndex,
        /* The kernel's bit, carried straight through, so the board and the CARDS panel
                   cannot disagree about which cards are the kernel's. */
        deletable: slot.wire.deletable,
        /* The kernel's verdict for the card, from the same overlay the CARDS row reads. */
        activity: cardActivityOf(track, slot.card.id),
        notice: notices.get(slot.card.id) ?? null,
      }));
  }, [cardRegistry, cards, track, notices]);
  const openableCards = useMemo(() => new Set(gridItems.map((item) => item.card.id)), [gridItems]);
  const existingCardIds = useMemo(() => new Set([...cards.map(card => card.id), ...snapshot.sessions.map(session => session.id)]), [cards, snapshot.sessions]);
  const defaultTerminalIds = useMemo(() => workbenchTerminalCardIds({ cards: panelCards, existingCardIds, tasks,
    activity: track, openableCards }), [panelCards, existingCardIds, tasks, track, openableCards]);
  /* `'rows'` unconditionally: an empty list with a `+` over it is the whole feature. */
  const trackViews = useTrackViews();
  if (trackViews === null) throw new Error('Terminal workbench requires TrackViewProvider');
  const workbench = useTerminalWorkbench({ session: trackViews.terminals.forTrack(track.id), trackId: track.id, cards, defaultCardIds: defaultTerminalIds, ordinaryCardOpen: requestedCardId !== null,
    onHide: () => { if (requestedCardId !== null && cards.some(card => card.id === requestedCardId && card.kind === 'terminal')) goSameTrack(track.id, { card: undefined }, { replace: true }); },
    create: (body, key) => trackMutations.createTerminal(track.id, body, key),
    request: () => ({ key: mintIdempotencyKey(), body: { theme: readHostThemeRgb() } }),
  });
  const chat = useConversationPanel(
    transport,
    unauthorized,
    {
      scopeId: track.id,
      rows,
      defaultProvider: plannerCard === undefined ? undefined : agentSessionDtoToConversation(plannerCard).provider,
      pergaMode: isPergaTrack,
      /* The track's own overlay-derived verdicts (`toTrack(detail, detailActivity)`). */
      cards: track.cards,
      /* These rows are on a track the reader can be sent to, so Today may hold and
               open them; the store checks each row's `trackId` against this. */
      rememberOn: track.id,
      workspaceRoot: track.agentCwd,
      scopeOf: (conversationId) => {
        const row = rows.find(candidate => candidate.id === conversationId);
        /* `id: row.trackId`, never `track.id`: this is the line the `rememberOn`
         * comparison rests on, and written the other way it would be a tautology. */
        return row === undefined ? null : {
          id: row.trackId,
          provider: row.provider,
          title: trackTitle, cardId: row.id, cardTitle: row.title,
          updatedAt: row.updatedAt, kind: row.kind, state: row.state, isLead: row.isLead,
        };
      },
      create: conversationMutations.create,
      pendingApprovals,
      decideApproval: (approval, optionId) => trackMutations.decideApproval(track.id, approval, optionId),
      planner: plannerCard === undefined ? null : {
        cardId: plannerCard.id, areaId: track.areaId, asks: openAsks,
        answerAsk: (askId, answers) => {
          const ask = openAsks.find(item => item.askId === askId);
          return trackMutations.answerAsk(track.id, askId, answers, ask?.questions);
        },
        role: plannerRole,
        commands: plannerCommands,
      },
    },
    { workspace: { waiting: workspaceWaiting, notice: workspaceNotice }, defaultRowId: center !== undefined && plannerCard !== undefined ? plannerCard.id : null, onPresent: center?.onOpenAssistant, presented: centerActive ? center?.presented : undefined, embedded: center === undefined ? undefined : { active: centerActive, host: centerHost, footerPrefix: center.footerPrefix, emptyIntroduction: center.emptyIntroduction, label: center.title }, onArchive: conversationMutations.archive, terminal: { workbench, render: () => <TerminalWorkbenchView workbench={workbench} host={trackCardHost}
      items={workbench.ordinaryHost ? [] : gridItems.filter(item => workbench.cardIds.has(item.card.id))} visible={!compactViewport && workbench.open}
      onRemove={(id) => cardDeletion.request(id, { kind: 'surface-center' })} /> }, showTrack: false, reportComposer: { ready: true, returnLabel: routePanel === 'conversations' ? 'Conversations' : 'Report' } },
  );
  useMobileConversationOwner({ track, conversations: rows, onArchive: conversationMutations.archive, onNew: chat.startConversation,
    selectedConversationId: chat.selectedConversationId, onClose: chat.close, onOpen: chat.openConversation, conversationOpen: chat.isOpen });
  /* One query per `chart.series` block, keyed by the block's rev; opening the
       report is what makes the kernel fetch the data. */
  const resolveSeries = useReportSeriesResolver(transport, track.id, reportBlocks, unauthorized);
  /* One polled read of the track's previews, only while the report has a `preview` block. */
  const resolvePreview = useReportPreviewResolver(transport, track.id, reportBlocks, unauthorized, isPergaTrack);
  const previewViewports = useReportPreviewViewports(track.id);
  /* The cards this route can open, asked of the registry through the list the board
   * draws. A worker card whose kind no entry claims is `unknown` and its `?card=`
   * is bounced; its task keeps its `workerCardId` regardless, because the TASKS
   * row looks its activity verdict up by that id. */
  const knownCard = requestedCardId !== null
    && gridItems.some((item) => item.card.id === requestedCardId);
  useEffect(() => {
    if (requestedCardId === null || !toolsQuery.isSuccess || knownCard) return;
    // Bouncing an unopenable `?card=` must drop only that parameter: the panel,
    // return surface and block anchor describe where the reader is.
    goSameTrack(track.id, { card: undefined }, { replace: true });
  }, [goSameTrack, knownCard, requestedCardId, toolsQuery.isSuccess, track.id]);
  // A card URL is an open intent, including on reload while secondary reads are pending.
  // Admit it once per desktop visit; ordinary rerenders must not reopen a hidden view.
  const admittedTerminalRoute = useRef<string | null>(null);
  useEffect(() => {
    if (compactViewport || requestedCardId === null) { admittedTerminalRoute.current = null; return; }
    if (!workbench.cardIds.has(requestedCardId) || admittedTerminalRoute.current === requestedCardId) return;
    admittedTerminalRoute.current = requestedCardId;
    workbench.reveal(requestedCardId);
  }, [compactViewport, requestedCardId, workbench]);
  /* `?panel=` is a compact concept: above the breakpoint `TrackPage` would put
   * `inert` on the desktop panel. Corrected here with `replace`, and the
   * injection below is gated on the viewport too, because on a cold start this
   * effect has not run at first paint. */
  useEffect(() => {
    if (compactViewport || routePanel === null) return;
    goSameTrack(track.id, { panel: undefined }, { replace: true });
  }, [compactViewport, goSameTrack, routePanel, track.id]);
  /* One confirm for both delete gestures. Nothing navigates on success: the
   * `knownCard` effect bounces `?card=<deleted>` off the URL, and a `go()` here
   * would race it. */
  const cardDeletion = useDeleteConfirm(
    (cardId, signal) => trackMutations.removeCard(track.id, cardId, signal), writeFailureText(DELETE_FAILURES, DELETE_TEXT),
  );

  /* Adding a card: the menu is the registry's own list, so this route only decides
   * which endpoint a kind takes — `atomic` kinds have an endpoint named after the
   * kind, `generic` ones go through `POST /api/tracks/:id/cards` with the entry's
   * `claim` kind. An unhandled strategy throws rather than creating nothing. */
  const addMenuEntries = useMemo(() => {
    const entries = cardAddMenuEntries(cardRegistry);
    return isPergaTrack
      ? entries.filter((entry) => entry.type === 'terminal' || entry.type === 'file-viewer')
      : entries;
  }, [cardRegistry, isPergaTrack]);
  const listDirectory = useMemo(
    () => createDirectoryLister(transport, unauthorized, isPergaTrack),
    [transport, unauthorized, isPergaTrack],
  );
  const reportFiles = useMemo(
    () => createTrackWorkspaceFilesPort(transport, unauthorized, track.id, shareAccess?.shareLinkId),
    [track.id, transport, unauthorized, shareAccess?.shareLinkId],
  );
  const [cardDraft, setCardDraft] = useState<(CardAddMenuEntry & { origin: DialogOrigin }) | null>(null);
  const [creatingCard, setCreatingCard] = useState(false);
  /* A failed create has to be sayable with no dialog on screen: a fieldless kind
   * never opens one, so the message gets a route-level surface. */
  const cardCreateFeedback = useOperationFeedback();
  const terminalPlacementFeedback = useOperationFeedback();
  const newCardFieldRef = useRef<HTMLInputElement | null>(null);
  /* A create that lands after the reader has left must not steer them: one
   * `AbortController` per attempt, aborted on unmount, read before the navigation
   * and every state write. */
  const activeCardCreate = useRef<AbortController | null>(null);
  useEffect(() => () => { activeCardCreate.current?.abort(); }, []);

  /* One add-card intent is one `Idempotency-Key` (#2131): Try again, a Create of the same draft and a pick of the same
   * fieldless kind resend the held key and body, so a retry after a lost answer joins the card the first attempt made.
   * A final outcome releases it; another draft is a new intent. The policy is `new-card/create.ts`; this only wires it. */
  const cardIntent = useKeyedIntent<CardDraft, KeyedCardBody>(sameCardDraft);
  const cardCreatePort: CardCreatePort = {
    createTerminal: (body, key) => trackMutations.createTerminal(track.id, body, key),
    createCodex: (body, key) => trackMutations.createCodex(track.id, body, key),
    createClaude: (body, key) => trackMutations.createClaude(track.id, body, key),
    createCard: (body, key) => trackMutations.createCard(track.id, body, key),
  };

  /* The create navigates to the new card, the same landing `onOpenCard` gives. `resent`: the held request goes again. */
  const runCardCreate = (draft: CardDraft, keyed: KeyedRequest<CardDraft, KeyedCardBody> | null, resent: boolean) => {
    /* Only the newest gesture may own the landing: a superseded attempt is aborted so
           it neither steers nor clears a busy state that now belongs to the newer attempt. */
    activeCardCreate.current?.abort();
    const controller = new AbortController();
    activeCardCreate.current = controller;
    setCreatingCard(true);
    void cardCreateFeedback
      .run(
        Promise.resolve().then(() => sendCardCreate(cardCreatePort, draft, keyed)).then((card) => {
          if (keyed !== null) cardIntent.release(keyed);
          if (controller.signal.aborted) return;
          setCardDraft(null);
          goSameTrack(track.id, { card: card.id });
        }, (error: unknown) => {
          if (keyed !== null && cardCreateEnded(error, resent)) cardIntent.release(keyed);
          throw error;
        }),
        cardCreateFailureText(draft.entry.label, resent),
        () => controller.signal.aborted,
      )
      .finally(() => {
        /* Only the attempt that still owns the busy state may clear it; both ways of
                   losing ownership abort, so `aborted` is the whole test. */
        if (controller.signal.aborted) return;
        activeCardCreate.current = null;
        setCreatingCard(false);
      });
  };

  const submitNewCard = (entry: CardAddMenuEntry, values: NewCardValues) => {
    const draft: CardDraft = { entry, values };
    /* Read at click time from `<html data-theme>`, not `useTheme()`: subscribing
           would remount any live terminal on every theme toggle. A resent request keeps its first press's theme. */
    const body = keyedCardBodyOf(draft, readHostThemeRgb(), cardRegistry);
    if (body === null) { runCardCreate(draft, null, false); return; }
    const resent = cardIntent.held !== null && sameCardDraft(cardIntent.held.draft, draft);
    runCardCreate(draft, cardIntent.request(draft, () => body), resent);
  };
  const heldCardCreate = cardIntent.held;
  const retryCardCreate = heldCardCreate === null || creatingCard ? null
    : () => { runCardCreate(heldCardCreate.draft, heldCardCreate, true); };

  /* A kind with nothing to ask is created on the spot; one with fields opens the form. */
  const pickCardKind = (entry: CardAddMenuEntry, trigger: HTMLButtonElement | null, origin: DialogOrigin) => {
    cardCreateFeedback.clear();
    if (!compactViewport && entry.type === 'terminal') { workbench.newSession(trigger); return; }
    if (entry.fields.length === 0) submitNewCard(entry, {});
    else setCardDraft({ ...entry, origin });
  };
  const backlinksQuery = useQuery(trackBacklinksQueryOptions(transport, track.id, unauthorized, { enabled: !isPergaTrack }));
  const backlinks = backlinksQuery.data;
  const openableWorkerIds = useMemo(() => {
    if (!isPergaTrack) return openableCards;
    const next = new Set(openableCards);
    for (const row of rows) next.add(row.id);
    return next;
  }, [isPergaTrack, openableCards, rows]);

  /* Every report arrival follows the current committed content boundary. */
  const arrivalAnchorId = useRouteHash();
  const reportAnchorIntent = useRouterState({ select: (state) => state.location.state.ncReportAnchorIntent });
  useTrackReportArrival(arrivalAnchorId, reportAnchorIntent, reportPresentation.current);
  const openReportLink = (target: ReportLinkTarget) => {
    if (target.trackId === track.id) {
      // Same track: a move within the report, so the return surface survives and the
      // panel closes.
      go({ name: 'track', trackId: target.trackId, blockId: target.blockId ?? undefined, from: routeFrom });
      return;
    }
    // Another track: a real navigation. Nothing carries over.
    go({ name: 'track', trackId: target.trackId, blockId: target.blockId ?? undefined });
  };

  const rememberReportFile = (target: ReportFileLinkTarget) => {
    const relativePath = parseWorkspaceRelativeFilePath(target.path)?.path ?? null;
    if (relativePath === null) return null;
    setRecentFilePaths(recentFiles.record(track.id, relativePath));
    return relativePath;
  };

  const openReportFile = (target: ReportFileLinkTarget) => {
    const relativePath = parseWorkspaceRelativeFilePath(target.path)?.path ?? null;
    if (relativePath === null) return;
    if (requestedFilePath === null && document.activeElement instanceof HTMLElement) {
      fileReturnFocusRef.current = document.activeElement;
    }
    fileNavigation.openFile(track.id, relativePath);
  };

  const [sourceSelection, setSourceSelection] = useState<{
    trackId: string; target: ReportSourceLinkTarget; opener: HTMLElement | null; filePath: string | null; position: ReportSourcePosition | null;
  } | null>(null);
  if (sourceSelection !== null && sourceSelection.filePath !== requestedFilePath) setSourceSelection(null);
  const openSource = (trackId: string, target: ReportSourceLinkTarget) => {
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    setSourceSelection({ trackId, target, opener, filePath: requestedFilePath, position: reportSourcePosition(opener) });
  };
  const openReportSource = (target: ReportSourceLinkTarget) => { openSource(track.id, target); };
  const linkPreview = { files: reportFiles, trackId: track.id, report,
    renderSource: (target: ReportSourceLinkTarget) => <ReportSourcePreview transport={transport} trackId={track.id} target={target} unauthorized={unauthorized} />,
    renderReference: (target: ReportLinkTarget) => <ReportReferencePreview transport={transport} unauthorized={unauthorized} target={target}
      onOpenLink={openReportLink} onOpenSource={openSource}
      onOpenFile={(owner, file) => { if (owner === track.id) openReportFile(file); else fileNavigation.openFile(owner, file.path); }} />,
  };
  const sourceInsertion = sourceSelection === null ? undefined : {
    position: sourceSelection.position,
    content: <ReportSourceInDocument key={`${sourceSelection.trackId}:${sourceSelection.target.destination}`}
      transport={transport} unauthorized={unauthorized} selection={sourceSelection} onClose={() => { setSourceSelection(null); }} />,
  };

  const closeBoard = () => {
    if (requestedFilePath !== null) {
      fileNavigation.closeFile(track.id);
      return;
    }
    go({ name: 'track', trackId: track.id, from: routeFrom }, { replace: true });
  };

  const boardOpen = knownCard || requestedFilePath !== null;

  const openReportAnchor = (blockId: string) => {
    go({ name: 'track', trackId: track.id, blockId, from: routeFrom });
  };

  const protocolClient = useQueryClient();
  const schedulesQuery = useQuery({ queryKey: trackSchedules(track.id), enabled: pergaMode,
    queryFn: ({ signal }) => runOperation(transport, { ...trackSchedulesOperation(track.id), signal }, unauthorized),
    refetchInterval: query => query.state.data?.recentRuns.some(run => run.state === 'pending' || run.state === 'delivering') ? 2000 : false });
  const templateQuery = useQuery({ queryKey: trackTemplate(track.id), enabled: pergaMode && !!track.templateId,
    queryFn: ({ signal }) => runOperation(transport, { ...trackTemplateOperation(track.id), signal }, unauthorized) });
  const protocolMutation = useRecoveryMutation(transport, {
    mutationFn: (operation: ApiOperation<unknown>, admitted) => runOperation(admitted, operation, unauthorized),
    onSuccess: async () => {
      await Promise.all([
        protocolClient.invalidateQueries({ queryKey: trackSchedules(track.id) }),
        protocolClient.invalidateQueries({ queryKey: trackTemplate(track.id) }),
        protocolClient.invalidateQueries({ queryKey: trackDetail(track.id) }),
      ]);
    },
  });
  const protocolWrite = async (operation: ApiOperation<unknown>) => { await protocolMutation.mutateAsync(operation); };
  const protocolWritable = pergaMode && shareAccess === null && snapshot.track.closedAtMs == null;

  const reportRegion = <>
    <TemplateProtocolBar track={track} schedules={schedulesQuery.data} inputsSchema={templateQuery.data?.manifest?.inputsSchema}
      loading={schedulesQuery.isLoading || templateQuery.isLoading}
      error={schedulesQuery.error?.message ?? templateQuery.error?.message}
      onSaveSchedule={protocolWritable ? request => protocolWrite(setTrackScheduleOperation(track.id, request)) : undefined}
      onCancelSchedule={protocolWritable ? id => protocolWrite(cancelTrackScheduleOperation(track.id, id)) : undefined}
      onTriggerSchedule={protocolWritable ? id => protocolWrite(triggerTrackScheduleOperation(track.id, id)) : undefined}
      onSaveInputs={protocolWritable ? patch => protocolWrite(updateTrackTemplateInputsOperation(track.id, patch)) : undefined} />
    {pergaMode && centerActive && reportPresentation.visible?.preparation.kind === 'empty' ? null : (
        <TrackReportRegion
        presentation={reportPresentation}
        sourceInsertion={requestedFilePath === null ? sourceInsertion : undefined}
        trackId={track.id}
        openedAt={navigationCompletedAt}
        resolveSeries={resolveSeries}
        resolvePreview={resolvePreview}
        previewViewports={previewViewports}
        taskVerdicts={verdicts}
        taskRows={tasks}
        renderTaskExecution={(task, expanded) => <TaskRecovery
          key={`${track.id}:${task.key}`} trackId={track.id} taskKey={task.key} expanded={expanded}
          transport={transport} unauthorized={unauthorized}
          enableTaskActions={isPergaTrack}
          openableWorkerIds={openableWorkerIds}
          openWorker={(cardId) => {
            if (isPergaTrack && rows.some((row) => row.id === cardId)) {
              registry.requestOpen(cardId, { focusComposer: false });
              return;
            }
            go({ name: 'track', trackId: track.id, cardId, from: routeFrom });
          }}
        />}
        rail={<ReportOutline items={outline} />}
        backlinkCounts={backlinks === undefined ? undefined : backlinkCountsByBlock(backlinks.backlinks)}
        onOpenLink={openReportLink}
        onOpenFileLink={openReportFile}
        onOpenSourceLink={openReportSource}
        onSaveProseBlock={report?.docRev !== undefined
          ? async (blockId, markdown) => {
            await trackMutations.updateReportBlock(track.id, blockId, {
              docRev: report.docRev!,
              knownBlockRev: report.blockRevs?.[blockId] ?? 1,
              kind: 'prose',
              markdown,
            });
          }
          : undefined}
        fileRoot={track.agentCwd}
        linkPreview={linkPreview}
        outlineEmpty={reportEmpty}
        empty={reportEmpty === undefined ? <ReportEmpty /> : reportEmpty}
      >{reportEvidence}</TrackReportRegion>
    )}
  </>;
  return (
    <>
    <TrackStage>
    <TrackPage
      fillReading={centerActive}
      showWorkspaceIsolation={showWorkspaceIsolation}
      shareLinks={!isPergaTrack && shareAccess === null ? <TrackShareLinks trackId={track.id} areaId={track.areaId} shareable={shareable} transport={transport} unauthorized={unauthorized} /> : undefined}
      onShareTrack={shareable && openShare !== null ? origin => openShare({ scope: { kind: 'track', id: track.id }, title: track.title, origin }) : undefined}
      progressDisclosure={progressDisclosure}
      panelHeader={pergaMode && panelContent !== undefined ? <div className={dailyStyles.calendarPanel}>{panelContent}</div> : undefined}
      panelContent={pergaMode ? (centerActive && reportPresentation.visible?.preparation.kind !== 'empty' ? <div className={dailyStyles.reportPanel}><PanelModule title="Daily report" icon="file">{reportRegion}</PanelModule></div> : undefined) : panelContent}
      mobileReportIntro={mobileReportIntro}
      mobilePanelObscured={!centerActive && chat.isOpen}
      mobileHeaderActionsHost={mobileHeaderActionsHost}
      mobileHeaderTitleHost={mobileHeaderTitleHost}
      mobileTitleReadView={shareAccess?.scope.kind === 'track' ? undefined : (controls) => <TrackSelector track={track} controls={controls}
        onOpenTracks={() => openMobileSection({ kind: 'tracks', areaId: track.areaId, returnTo: 'report' })} />}
      track={track}
      canReopenTrack={canReopenTrack}
      canCloseTrack={canCloseTrack}
      adminGrantedAt={adminGrantedAt}
      cards={panelCards}
      /* Derived from the report's own blocks, so the panel and the document
         cannot disagree about what tasks exist. */
      tasks={tasks}
      openableCards={openableCards}
      existingCardIds={existingCardIds}
      outlineItems={outline}
      /* Tasks and the mobile Outline share one anchor landing. The URL carries
         it too, so the reader can hand the destination to somebody else. */
      onOpenTask={openReportAnchor}
      onOpenOutline={openReportAnchor}
      cardsAction={<AddCardMenu entries={addMenuEntries} onSelect={pickCardKind} label="Tools" icon="more" />}
      recentFiles={recentFilePaths.length === 0 ? undefined : <RecentFiles inline
        paths={recentFilePaths}
        onOpen={(path) => openReportFile({ path })}
      />}
      onOpenCard={(cardId) => { if (!compactViewport && workbench.cardIds.has(cardId)) { workbench.reveal(cardId); return; } go({ name: 'track', trackId: track.id, cardId, from: routeFrom }); }}
      onDeleteCard={cardDeletion.request}
      onSetWorkbench={(cardId, inWorkbench) => {
        if (isPergaTrack) {
          preferences.setTerminalWorkbench(track.id, cardId, inWorkbench);
          return;
        }
        void terminalPlacementFeedback.run(trackMutations.setTerminalWorkbench(track.id, cardId, inWorkbench),
          () => 'Could not move this terminal. Try again.');
      }}
      board={<>
        <CardGridOverlay
          open={knownCard && (compactViewport || !workbench.cardIds.has(requestedCardId))}
          items={workbench.ordinaryHost ? gridItems : gridItems.filter(item => !workbench.cardIds.has(item.card.id))}
          host={trackCardHost}
          activeCardId={requestedCardId}
          onRemoveCard={(id) => cardDeletion.request(id, { kind: 'surface-center' })}
          onClose={knownCard ? closeBoard : undefined}
        />
        {requestedFilePath !== null && (
          <ReportFileViewer
            key={requestedFilePath}
            path={requestedFilePath}
            files={reportFiles}
            sourceInsertion={sourceInsertion}
            linkPreview={linkPreview}
            fileRoot={track.agentCwd}
            wide={gridItems.length === 0}
            onClose={closeBoard}
            onFileOpened={(path) => { rememberReportFile({ path }); }}
            onOpenFileLink={openReportFile}
            onOpenSourceLink={openReportSource}
          />
        )}
      </>}
      onCloseBoard={boardOpen ? closeBoard : undefined}
      /* Gated on the viewport too: the effect above cannot have run yet on a desktop
               cold start, and one render "open" is one render with the desktop panel `inert`. */
      panel={renderedMobilePanel(routePanel, {
        compact: compactViewport,
        overlayOpen: requestedCardId !== null || rawRequestedFilePath !== null,
      })}
      onOpenPanel={(kind) => { openPanel(track.id, kind); }}
      onClosePanel={() => { closePanel(track.id); }}
      /* `?from=` is the whole memory of how the reader got here; absent means Pages. */
      mobileBackLabel={routeFrom === 'area' ? 'Tracks' : 'Pages'}
      onMobileBack={shareAccess?.scope.kind === 'track' ? undefined : () => {
        if (routeFrom === 'area') openMobileSection({ kind: 'tracks', areaId: track.areaId });
        else openMobileSection({ kind: 'pages' });
      }}
      report={<>
        {pergaMode && reportHeader}
        {center !== undefined && <div className={mobileChatStyles.embeddedSlot} hidden={!centerActive}>
          {center.render(<div ref={setCenterHost} className={mobileChatStyles.embeddedSlot} />)}
        </div>}
        {!centerActive && <>{!pergaMode && reportHeader}{(!chat.isOpen || chat.selectedConversationId !== plannerCard?.id) && workspaceNotice}
        {canCloseTrack && shareAccess === null &&
          <WorkerDispatchControl key={track.id} trackId={track.id} transport={transport} unauthorized={unauthorized} />}
        {reportRegion}</>}
      </>}
      backlinks={backlinks !== undefined && (backlinks.backlinks.length > 0 || backlinks.truncated || backlinks.skipped_sources > 0)
        ? (
          <ReportBacklinks inline
            trackId={track.id}
            backlinks={backlinks}
            onOpen={(trackId, blockId) => {
              // Same track keeps the return surface; a backlink into another track
              // is a departure and carries nothing.
              go({ name: 'track', trackId, blockId, from: trackId === track.id ? routeFrom : undefined });
            }}
          />
        )
        : undefined}
      mobileChatComposer={chat.dock}
      conversationContextList={!pergaMode || !centerActive || rows.length > 1 ? chat.contextList : undefined}
      conversationList={!pergaMode || !centerActive || rows.length > 1 ? chat.list : undefined}
      onStartConversation={!pergaMode || !centerActive || rows.length > 1 ? chat.startConversation : undefined}
      showConversationAction={!centerActive}
      conversationOpen={!centerActive && chat.isOpen}
      unread={trackReceipts.isTrackUnread(track.id, track.notifiableSessions)}
      onReply={() => {
        const destination = priorityConversation(rows, track.cards,
          row => trackReceipts.isUnread('conversation', row.id, row.lastTurnCompletedAt ?? null));
        if (destination === undefined) { chat.startConversation(); return; }
        center?.onOpenAssistant();
        chat.openConversation(destination);
      }}
      onRenameTrack={(title) => trackMutations.patch(track.id, track.areaId, { title }).then(() => undefined)}
      closedControl={closedControl}
      onDeleteTrack={shareAccess === null ? (signal) => trackMutations.remove(track.id, track.areaId, signal) : undefined}
      onTrackDeleted={() => go({ name: 'today' })}
    />
    </TrackStage>
    {/* Keyed by kind: switching kinds is a different form, and a shared mount
        would carry the previous kind's typed values into it. */}
    <Dialog purpose="form" origin={cardDraft?.origin ?? { kind: 'surface-center' }}
      open={cardDraft !== null}
      onClose={() => setCardDraft(null)}
      title={cardDraft === null ? '' : `New ${cardDraft.label} card`}
      initialFocusRef={newCardFieldRef}
    >
      {cardDraft !== null && (
        <NewCardForm
          key={cardDraft.type}
          entry={cardDraft}
          submitting={creatingCard}
          error={cardCreateFeedback.error}
          onRetry={retryCardCreate}
          listDirectory={listDirectory}
          firstFieldRef={newCardFieldRef}
          onCancel={() => setCardDraft(null)}
          onSubmit={(values) => submitNewCard(cardDraft, values)}
        />
      )}
    </Dialog>
    <DeleteConfirmDialog confirm={cardDeletion} copy={DELETE_CARD_COPY} />
    <OperationFeedback feedback={terminalPlacementFeedback} />
    {/* Only while the dialog is closed: `NewCardForm` renders the same `error` inline. */}
    {cardDraft === null && <OperationFeedback feedback={cardCreateFeedback}
      action={retryCardCreate === null ? undefined : <Button label="Try again" variant="ghost" onClick={retryCardCreate} />} />}
    <div data-nc-conversation-drawer-host="">
      <div className={mobileChatStyles.history}>{chat.drawer}</div>
    </div>
    </>
  );
}
