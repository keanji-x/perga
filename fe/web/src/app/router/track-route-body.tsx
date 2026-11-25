import { terminalPanelItem } from '../../../../core/domain/panel-item.ts';
import type { TrackAggregateSnapshotDto } from "../../../../core/api/perga-v1.ts";
import { priorityConversation } from '../../../../core/domain/activity.ts';
import { DELETE_FAILURES,DELETE_TEXT,writeFailureText } from '../../../../core/domain/failure-class.ts';
import { terminalPresentation } from '../../../../core/domain/terminal-presentation.ts';
import { TemplateProtocolBar } from '../../features/report/template-protocol/public.tsx';
import { useTrackClosedControl } from '../../features/track/page/closed-control.ts';
import { TerminalWorkbenchView,useTerminalWorkbench } from '../../features/track/workbench/public.tsx';
import { PanelModule } from '../../ui/panel-card/public.tsx';
import { trackCommandBindings } from './track-composer-command.ts';

import { useQuery } from '@tanstack/react-query';
import {
useRouterState
} from '@tanstack/react-router';
import { useCallback,useEffect,useLayoutEffect,useMemo,useRef,type ReactNode } from 'react';
import type { ReportPreparationPort } from '../../features/report/document/preparation.ts';
import { TrackReportRegion,useTrackReportArrival,useTrackReportPresentation } from './track-report-presentation.tsx';
import { useTrackViews,useTrackViewState } from './track-view-state.tsx';

import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { cardActivityOf } from '../../../../core/domain/activity.ts';
import { NO_PENDING_APPROVALS } from '../../../../core/domain/approval.ts';
import { openAsksOf } from '../../../../core/domain/ask.ts';
import {
agentSessionDtoToConversation
} from '../../../../core/domain/conversation.ts';
import {
parseWorkspaceRelativeFilePath,type ReportFileLinkTarget,
} from '../../../../core/domain/report-file.ts';
import type { ReportSourceLinkTarget } from '../../../../core/domain/report-source.ts';
import type { ReportLinkTarget } from '../../../../core/domain/report.ts';
import { trackTaskInventory } from '../../../../core/domain/track-task-inventory.ts';
import {
toTrack,trackActivityFromSnapshot,trackDisplayTitle
} from '../../../../core/domain/track.ts';
import { parkedWorkerNotices,workbenchTerminalCardIds } from '../../../../core/view/track-page.ts';
import { ReportEmpty } from '../../features/report/empty/public.tsx';
import { ReportFileViewer } from '../../features/report/file-viewer/public.tsx';
import { ReportOutline } from '../../features/report/outline/public.tsx';
import { RecentFiles } from '../../features/report/recent-files/public.tsx';
import { reportSourcePosition,type ReportSourcePosition } from '../../features/report/source/placement.ts';
import { CardPanelOverlay,TrackStage } from '../../features/track/grid/public.tsx';
import {
sameCardDraft,
type CardDraft,type KeyedCardBody
} from '../../features/track/new-card/create.ts';
import { TrackPage } from '../../features/track/page/public.tsx';
import type {
CardHostItem
} from '../../systems/cards/public.js';
import {
createCardHost,partitionTrackCards
} from '../../systems/cards/public.js';
import { DELETE_CARD_COPY } from '../../features/track/delete-copy.ts';
import { DeleteConfirmDialog,useDeleteConfirm } from '../../ui/operation-feedback/public.tsx';
import { useState } from '../../ui/state/public.ts';
import { useCompactViewport } from '../../ui/viewport/public.ts';
import {
useConversationCommands
} from '../conversations/public.tsx';
import { createDirectoryLister,createTrackCardFilesPort,createTrackWorkspaceFilesPort } from '../providers/directory.ts';
import { mintIdempotencyKey,useKeyedIntent } from '../providers/idempotency-key.ts';
import { useReadReceipts } from '../providers/read-receipts.tsx';
import {
type RecentFileHistory
} from '../providers/recent-files.ts';
import { useTrackConversationMutations } from '../providers/track-conversation-mutations.ts';
import { useTrackMutations } from '../providers/track-mutations.ts';
import { trackToolsQueryOptions } from '../providers/track-queries.ts';
import { useUiPreferences } from '../providers/ui-preferences.tsx';
import { useMobileConversationOwner,useMobileHeaderActionsHost,useMobileHeaderTitleHost,useOpenMobileSection } from '../shell/public.tsx';
import { TrackSelector } from '../shell/track-selector.tsx';
import dailyStyles from './daily-planner.module.css';
import { type DailyCenterPresentation } from './daily-planner.tsx';
import mobileChatStyles from './mobile-chat.module.css';
import {
renderedMobilePanel,
useGo,useGoSameTrack,
usePlannerOpenIntent,
useRouteCardId,useRouteFilePath,useRouteFrom,useRouteHash,
useRoutePanel,
useTrackFileNavigation,
useTrackPanelNavigation
} from './navigation.ts';
import { ReportReferencePreview } from './report-reference.tsx';
import { ReportSourceInDocument,ReportSourcePreview } from './report-source.tsx';
import { useTrackTemplateViews } from './report-views.ts';

import { useNewTrackCard } from '../../features/track/new-card/controller.tsx';
import type { CardRuntime } from './card-runtime.ts';
import { useConversationPanel } from './conversation-panel.tsx';
export function TrackRouteBody({
  transport, unauthorized, snapshot, memoryAreaId, trackPath, showWorkspaceIsolation = true, cardRuntime, recentFiles, reportEvidence, reportHeader, reportEmpty, panelContent, mobileReportIntro, prepareReport, center, workspaceWaiting = false, workspaceNotice,
}: {
  transport: ApiTransportPort;
  unauthorized: UnauthorizedChannel;
  snapshot: TrackAggregateSnapshotDto;
  memoryAreaId?: string | null | false;
  trackPath?: string;
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

}) {
  const track = useMemo(() => toTrack(snapshot.track, trackActivityFromSnapshot(snapshot)), [snapshot]);
  const isMemoryTrack = track.areaId === 'sys_memory';
  const canReopenTrack = !isMemoryTrack && snapshot.track.closedAtMs != null;
  const canCloseTrack = !isMemoryTrack && !canReopenTrack;
  const mobileAreaId = isMemoryTrack ? (typeof memoryAreaId === 'string' ? memoryAreaId : null) : track.areaId;
  const alias = useMemo(() => trackPath === undefined ? undefined : { trackId: track.id, path: trackPath }, [track.id, trackPath]);
  const toolsQuery = useQuery(trackToolsQueryOptions(transport, snapshot.track.id, unauthorized));
  const rawCards = useMemo(() => (toolsQuery.data ?? []).map((terminal, index) => terminalPanelItem(terminal, 100 + index)), [toolsQuery.data]);
  const [centerHost, setCenterHost] = useState<HTMLDivElement | null>(null);
  const preferences = useUiPreferences();
  const cards = (() => {

    const withPlacement = rawCards.map((card) => {
      if (card.kind !== 'terminal') return card;
      const visible = preferences.terminalWorkbench(track.id, card.id);
      if (visible === null || visible === terminalPresentation(card).inWorkbench) return card;
      return { ...card, presentation: { ...terminalPresentation(card), inWorkbench: visible } };
    });
    return withPlacement.some((card, index) => card !== rawCards[index]) ? withPlacement : rawCards;
  })();
  const reportPresentation = useTrackReportPresentation(track.id, snapshot.report, prepareReport);
  const progressDisclosure = useTrackViewState(track.id, reportPresentation.presentable);
  // The same key and comparison point the rail uses: the overlay's completion
  // high-water mark, never the row's `updatedAt`.
  // A completed navigation also acknowledges a repeated selection of this already rendered Track.
  const navigationCompletedAt = useRouterState({ select: (state) => state.loadedAt });
  const trackMutations = useTrackMutations(transport, unauthorized);
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
  const go = useGo(alias);
  const goSameTrack = useGoSameTrack(alias);
  const fileNavigation = useTrackFileNavigation(alias);
  const { openPanel, closePanel } = useTrackPanelNavigation(alias);
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
    if (document.activeElement !== document.body) return;
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
  const mobileBackToArea = isMemoryTrack ? mobileAreaId !== null : routeFrom === 'area';
  const cardRegistry = cardRuntime.registry;
  const trackCardHost = useMemo(
    () => createCardHost(cardRegistry, {
          files: createTrackCardFilesPort(transport, unauthorized, track.id, track.agentCwd),
        }),
    [cardRegistry, transport, unauthorized, track.id, track.agentCwd],
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
  /* The CARDS module lists cards that have a surface; unclaimed kinds stay listed,
   * and `originalIndex` is bound before the filter so the merge keeps item order. */
  const panelCards = useMemo(() => {
    const { visible, unknown } = partitionTrackCards(cardRegistry, cards);
    return [...visible, ...unknown]
      .sort((left, right) => left.originalIndex - right.originalIndex)
      .map((slot) => slot.item);
  }, [cardRegistry, cards]);
  const { report, outline } = reportPresentation;
  const reportBlocks = report?.blocks ?? null;
  const tasks = useMemo(() => trackTaskInventory(reportBlocks, snapshot.tasks), [reportBlocks, snapshot.tasks]);
  /* Compare notices by content so unrelated task updates keep the board items stable. */
  const noticeKey = JSON.stringify([...parkedWorkerNotices(tasks)]);
  const notices = useMemo(() => new Map(JSON.parse(noticeKey) as [string, string][]), [noticeKey]);
  const panelItems: readonly CardHostItem[] = useMemo(() => {
    const { visible } = partitionTrackCards(cardRegistry, cards);
    return [...visible]
      .sort((left, right) => {
        const sort = left.item.sort - right.item.sort;
        return sort !== 0 ? sort : left.originalIndex - right.originalIndex;
      })
      .map((slot) => Object.freeze({
        card: slot.card,
        title: slot.item.title ?? slot.item.kind,
        originalIndex: slot.originalIndex,
        /* The same client removal capability drives the board and inventory. */
        deletable: slot.item.removable,
        /* The kernel's verdict for the card, from the same overlay the CARDS row reads. */
        activity: cardActivityOf(track, slot.card.id),
        notice: notices.get(slot.card.id) ?? null,
      }));
  }, [cardRegistry, cards, track, notices]);
  const openableCards = useMemo(() => new Set(panelItems.map((item) => item.card.id)), [panelItems]);
  const existingCardIds = useMemo(() => new Set([...cards.map(card => card.id), ...snapshot.sessions.map(session => session.id)]), [cards, snapshot.sessions]);
  const defaultTerminalIds = useMemo(() => workbenchTerminalCardIds({ cards: panelCards, existingCardIds, tasks,
    activity: track, openableCards }), [panelCards, existingCardIds, tasks, track, openableCards]);
  /* `'rows'` unconditionally: an empty list with a `+` over it is the whole feature. */
  const trackViews = useTrackViews();
  if (trackViews === null) throw new Error('Terminal workbench requires TrackViewProvider');
  const workbench = useTerminalWorkbench({ session: trackViews.terminals.forTrack(track.id), trackId: track.id, cards, defaultCardIds: defaultTerminalIds, ordinaryCardOpen: requestedCardId !== null,
    onHide: () => { if (requestedCardId !== null && cards.some(card => card.id === requestedCardId && card.kind === 'terminal')) goSameTrack(track.id, { card: undefined }, { replace: true }); },
    create: (body, key) => trackMutations.createTerminal(track.id, body, key),
    request: () => ({ key: mintIdempotencyKey(), body: {} }),
  });
  const chat = useConversationPanel(
    transport,
    unauthorized,
    {
      scopeId: track.id,
      rows,
      defaultProvider: plannerCard === undefined ? undefined : agentSessionDtoToConversation(plannerCard).provider,
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
        cardId: plannerCard.id, areaId: track.areaId, memoryAreaId, asks: openAsks,
        answerAsk: (askId, answers) => {
          const ask = openAsks.find(item => item.askId === askId);
          return trackMutations.answerAsk(track.id, askId, answers, ask?.questions);
        },
        commands: plannerCommands,
      },
    },
    { workspace: { waiting: workspaceWaiting, notice: workspaceNotice }, defaultRowId: center !== undefined && plannerCard !== undefined ? plannerCard.id : null, onPresent: center?.onOpenAssistant, presented: centerActive ? center?.presented : undefined, embedded: center === undefined ? undefined : { active: centerActive, host: centerHost, footerPrefix: center.footerPrefix, emptyIntroduction: center.emptyIntroduction, label: center.title }, onArchive: conversationMutations.archive, terminal: { workbench, render: () => <TerminalWorkbenchView workbench={workbench} host={trackCardHost}
      items={workbench.ordinaryHost ? [] : panelItems.filter(item => workbench.cardIds.has(item.card.id))} visible={!compactViewport && workbench.open}
      onRemove={(id) => cardDeletion.request(id, { kind: 'surface-center' })} /> }, showTrack: false, reportComposer: { ready: true, returnLabel: routePanel === 'conversations' ? 'Conversations' : 'Report' } },
  );
  useMobileConversationOwner({ track, conversations: rows, onArchive: conversationMutations.archive, onNew: chat.startConversation,
    selectedConversationId: chat.selectedConversationId, onClose: chat.close, onOpen: chat.openConversation, conversationOpen: chat.isOpen });
  const templateViews = useTrackTemplateViews(transport, track.id, unauthorized);
  /* The cards this route can open, asked of the registry through the list the board
   * draws. A worker card whose kind no entry claims is `unknown` and its `?card=`
   * is bounced; its task keeps its `workerCardId` regardless, because the TASKS
   * row looks its activity verdict up by that id. */
  const knownCard = requestedCardId !== null
    && panelItems.some((item) => item.card.id === requestedCardId);
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

  const reportFiles = useMemo(
    () => createTrackWorkspaceFilesPort(transport, unauthorized, track.id),
    [track.id, transport, unauthorized],
  );
  const cardCreation = useNewTrackCard({ registry: cardRegistry,
    cardCreatePort: { createTerminal: (body, key) => trackMutations.createTerminal(track.id, body, key) },
    cardIntent: useKeyedIntent<CardDraft, KeyedCardBody>(sameCardDraft),
    directory: useMemo(() => createDirectoryLister(transport, unauthorized), [transport, unauthorized]),
    onCreated: cardId => goSameTrack(track.id, { card: cardId }),
    compactViewport, newTerminalSession: workbench.newSession,
  });
  const openableWorkerIds = useMemo(() => {

    const next = new Set(openableCards);
    for (const row of rows) next.add(row.id);
    return next;
  }, [openableCards, rows]);

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
    go({ name: 'track', trackId: track.id, from: routeFrom }, { replace: true });
  };
  const closeFile = () => { fileNavigation.closeFile(track.id); };

  const openReportAnchor = (blockId: string) => {
    go({ name: 'track', trackId: track.id, blockId, from: routeFrom });
  };
  const openRelatedTrack = (trackId: string) => { go({ name: 'track', trackId }); };

  const hasFlowContent = templateViews?.status === 'ready'
    && templateViews.projection.flow != null
    && (templateViews.projection.flow.nodes.length > 0 || templateViews.projection.flow.archived.length > 0);
  const hasDailyReportContent = reportPresentation.visible?.preparation.kind !== 'empty' || hasFlowContent;

  const reportRegion = <>
    <TemplateProtocolBar track={track} />
    {centerActive && !hasDailyReportContent ? null : (
        <TrackReportRegion
        presentation={reportPresentation}
        sourceInsertion={requestedFilePath === null ? sourceInsertion : undefined}
        trackId={track.id}
        openedAt={navigationCompletedAt}
        templateViews={templateViews}
        taskRows={tasks}
        rail={<ReportOutline items={outline} />}

        onOpenLink={openReportLink}
        onOpenFileLink={openReportFile}
        onOpenTrack={openRelatedTrack}
        onOpenWorker={(cardId) => {
          if (!openableWorkerIds.has(cardId)) return;
          if (rows.some((row) => row.id === cardId)) {
            registry.requestOpen(cardId, { focusComposer: false });
            return;
          }
          go({ name: 'track', trackId: track.id, cardId, from: routeFrom });
        }}
        onOpenSourceLink={openReportSource}
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
      articleMasthead={center === undefined}
      showWorkspaceIsolation={showWorkspaceIsolation}
      shareLinks={undefined}
      progressDisclosure={progressDisclosure}
      panelHeader={panelContent !== undefined ? <div className={dailyStyles.calendarPanel}>{panelContent}</div> : undefined}
      panelContent={centerActive && hasDailyReportContent ? <div className={dailyStyles.reportPanel}><PanelModule title="Daily report" icon="file">{reportRegion}</PanelModule></div> : undefined}
      mobileReportIntro={mobileReportIntro}
      mobilePanelObscured={!centerActive && chat.isOpen}
      mobileHeaderActionsHost={mobileHeaderActionsHost}
      mobileHeaderTitleHost={mobileHeaderTitleHost}
      mobileTitleReadView={(controls) => <TrackSelector track={track} controls={controls}
        onOpenTracks={() => openMobileSection(mobileAreaId === null
          ? { kind: 'pages' }
          : { kind: 'tracks', areaId: mobileAreaId, returnTo: 'report' })} />}
      track={track}
      onOpenOriginTrack={openRelatedTrack}
      canReopenTrack={canReopenTrack}
      canCloseTrack={canCloseTrack}
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
      cardsAction={cardCreation.menu}
      recentFiles={recentFilePaths.length === 0 ? undefined : <RecentFiles inline
        paths={recentFilePaths}
        onOpen={(path) => openReportFile({ path })}
      />}
      onOpenCard={(cardId) => { if (!compactViewport && workbench.cardIds.has(cardId)) { workbench.reveal(cardId); return; } go({ name: 'track', trackId: track.id, cardId, from: routeFrom }); }}
      onDeleteCard={cardDeletion.request}
      onSetWorkbench={(cardId, inWorkbench) => {

          preferences.setTerminalWorkbench(track.id, cardId, inWorkbench);
          return;

      }}
      board={
        <CardPanelOverlay
          open={knownCard && (compactViewport || !workbench.cardIds.has(requestedCardId))}
          items={workbench.ordinaryHost ? panelItems : panelItems.filter(item => !workbench.cardIds.has(item.card.id))}
          host={trackCardHost}
          activeCardId={requestedCardId}
          onRemoveCard={(id) => cardDeletion.request(id, { kind: 'surface-center' })}
          onClose={knownCard ? closeBoard : undefined}
        />}
      onCloseBoard={knownCard ? closeBoard : undefined}
      fileViewer={requestedFilePath === null ? undefined : {
        onClose: closeFile,
        render: hasBackFocus => (
          <ReportFileViewer
            key={requestedFilePath}
            path={requestedFilePath}
            files={reportFiles}
            sourceInsertion={sourceInsertion}
            linkPreview={linkPreview}
            fileRoot={track.agentCwd}
            wide
            onClose={closeFile}
            hasBackFocus={hasBackFocus}
            onFileOpened={(path) => { rememberReportFile({ path }); }}
            onOpenFileLink={openReportFile}
            onOpenSourceLink={openReportSource}
          />
        ),
      }}
      /* Gated on the viewport too: the effect above cannot have run yet on a desktop
               cold start, and one render "open" is one render with the desktop panel `inert`. */
      panel={renderedMobilePanel(routePanel, {
        compact: compactViewport,
        overlayOpen: requestedCardId !== null || rawRequestedFilePath !== null,
      })}
      onOpenPanel={(kind) => { openPanel(track.id, kind); }}
      onClosePanel={() => { closePanel(track.id); }}
      /* `?from=` is the whole memory of how the reader got here; absent means Pages. */
      mobileBackLabel={mobileBackToArea ? 'Tracks' : 'Pages'}
      onMobileBack={() => {
        if (mobileBackToArea && mobileAreaId !== null) openMobileSection({ kind: 'tracks', areaId: mobileAreaId });
        else openMobileSection({ kind: 'pages' });
      }}
      report={<>
        {reportHeader}
        {center !== undefined && <div className={mobileChatStyles.embeddedSlot} hidden={!centerActive}>
          {center.render(<div ref={setCenterHost} className={mobileChatStyles.embeddedSlot} />)}
        </div>}
        {!centerActive && <>{(!chat.isOpen || chat.selectedConversationId !== plannerCard?.id) && workspaceNotice}
        {reportRegion}</>}
      </>}
      mobileChatComposer={chat.dock}
      conversationContextList={!centerActive || rows.length > 1 ? chat.contextList : undefined}
      conversationList={!centerActive || rows.length > 1 ? chat.list : undefined}
      onStartConversation={!centerActive || rows.length > 1 ? chat.startConversation : undefined}
      conversationOpen={!centerActive && chat.isOpen}
      unread={trackReceipts.isTrackUnread(track.id, track.notifiableSessions)}
      onReply={() => {
        const destination = priorityConversation(rows, track.cards,
          row => trackReceipts.isUnread('conversation', row.id, row.lastTurnCompletedAt ?? null));
        if (destination === undefined) { chat.startConversation(); return; }
        center?.onOpenAssistant();
        chat.openConversation(destination);
      }}
      onRenameTrack={isMemoryTrack ? undefined : (title) => trackMutations.patch(track.id, track.areaId, { title }).then(() => undefined)}
      closedControl={closedControl}
      onDeleteTrack={isMemoryTrack ? undefined : (signal) => trackMutations.remove(track.id, track.areaId, signal)}
      onTrackDeleted={() => go({ name: 'today' })}
    />
    </TrackStage>
    {cardCreation.surfaces}
    <DeleteConfirmDialog confirm={cardDeletion} copy={DELETE_CARD_COPY} />
    <div data-nc-conversation-drawer-host="">
      <div className={mobileChatStyles.history}>{chat.drawer}</div>
    </div>
    </>
  );
}
