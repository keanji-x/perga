import { useRouter, type RouterHistory } from '@tanstack/react-router';
import { useQuery } from '@tanstack/react-query';
import { useCallback, useMemo, useRef } from 'react';

import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import type { Area, NewAreaBody } from '../../../../core/domain/area.ts';
import {
  AREA_CREATE_FAILURES, AREA_CREATE_TEXT, AREA_PATCH_FAILURES, AREA_PATCH_TEXT, visibleAreas,
} from '../../../../core/domain/area.ts';
import {
  ApiError, classifyFailure, NotSentError, refusedText, writeFailureOf, writeFailureText,
} from '../../../../core/domain/failure-class.ts';
import { MARK_UNREAD_TEXT, READ_RECEIPT_FAILURES } from '../../../../core/domain/read-receipts.ts';
import type { Track } from '../../../../core/domain/track.ts';
import { TRACK_PATCH_FAILURES, TRACK_PATCH_TEXT } from '../../../../core/domain/track.ts';
import type { AreaEditorPatch, AreaEditorValues } from '../../features/area/editor/public.tsx';
import { AREA_PALETTE } from '../../features/area/palette.ts';
import type { DialogOrigin } from '../../ui/dialog/public.tsx';
import { useOperationFeedback } from '../../ui/operation-feedback/public.tsx';
import { useState } from '../../ui/state/public.ts';
import { AreaCreatePreflightError, useAreaMutations } from '../providers/area-mutations.ts';
import { createDirectoryLister } from '../providers/directory.ts';
import { executionTargetsQueryOptions, useCreateExecutionTarget } from '../providers/execution-target-queries.ts';
import { useKeyedIntent, type KeyedRequest } from '../providers/idempotency-key.ts';
import { workspaceActivityErrorText, workspaceReadErrorText } from '../providers/query-read-feedback.ts';
import { useReadReceipts } from '../providers/read-receipts.tsx';
import { useTrackLifecycleMutations } from '../providers/track-lifecycle-mutations.ts';
import { useUiPreferences } from '../providers/ui-preferences.tsx';
import { useWorkspace } from '../providers/workspace-queries.ts';
import {
  routeParamFromPath, useCurrentPath, useGo, useMobileFocusHomeLocation, useNewTrackBack, useRouteCardId, useRouteFilePath,
  useTrackPanelNavigation,
} from '../router/navigation.ts';
import { useNewTrackDraftArea, usePrepareNewTrack } from '../router/new-track-drafts.tsx';
import { useDrawerWidthHost } from './drawer-width.tsx';
import { settingsSectionForPath } from './settings-overlay.tsx';
import { sidebarTrackGroups } from './sidebar-track-groups.ts';

export type AreaEditorTarget =
  | Readonly<{ kind: 'create'; origin: DialogOrigin }>
  | Readonly<{ kind: 'edit'; area: Area; origin: DialogOrigin }>;

function randomAreaColor(): string {
  return AREA_PALETTE[Math.floor(Math.random() * AREA_PALETTE.length)] ?? AREA_PALETTE[0];
}

export type ShellControllerOptions = Readonly<{
  transport: ApiTransportPort;
  unauthorized: UnauthorizedChannel;
  nowMs?: number;
  onAreaCreated?: (area: Area) => void;
}>;

/** Shared headless shell kernel: workspace reads, mutations, read receipts, Area editor state, and routing actions. */
export function useShellController({
  transport, unauthorized, nowMs, onAreaCreated,
}: ShellControllerOptions) {
  const router = useRouter();
  const workspace = useWorkspace(transport, unauthorized);
  const areaMutations = useAreaMutations(transport, unauthorized);
  const prepareNewTrack = usePrepareNewTrack();
  const trackMutations = useTrackLifecycleMutations(transport, unauthorized);
  const pinFeedback = useOperationFeedback();
  const listDirectory = createDirectoryLister(transport, unauthorized);
  const executionTargetsQuery = useQuery({
    ...executionTargetsQueryOptions(transport, unauthorized),
    retry: false,
  });
  const createExecutionTarget = useCreateExecutionTarget(transport, unauthorized);
  const listDirectoryForTarget = useCallback(
    (targetId: string) => createDirectoryLister(transport, unauthorized, targetId),
    [transport, unauthorized],
  );

  /* The create form is locked while a request is held, so every press resends the held key and body (#2131). */
  const areaIntent = useKeyedIntent<null, NewAreaBody>(() => true);
  const areaCreateRequest = areaIntent.held;
  const [areaEditorTarget, setAreaEditorTarget] = useState<AreaEditorTarget | null>(null);
  const [areaEditorPending, setAreaEditorPending] = useState(false);
  const [areaEditorError, setAreaEditorError] = useState<string | null>(null);
  const areaEditorNameRef = useRef<HTMLInputElement | null>(null);

  const currentPath = useCurrentPath();
  const routeOwnsMobileChrome = useMobileFocusHomeLocation();
  const settingsOpen = settingsSectionForPath(currentPath) !== null;
  const routeCardId = useRouteCardId();
  const routeFilePath = useRouteFilePath();
  const mobileOverlayRoute = typeof routeCardId === 'string' || typeof routeFilePath === 'string';
  const go = useGo();
  const newTrackBack = useNewTrackBack();
  // The report's panel is a history destination, so the shell leaves it the same way the report does.
  const { closePanel } = useTrackPanelNavigation();

  const readError = workspaceReadErrorText(workspace);
  const activityError = workspaceActivityErrorText(workspace);
  const readLoading = workspace.areasLoading
    || [...workspace.tracksLoadingByArea.values()].some(Boolean);
  const retryRead = () => {
    workspace.retryAreas();
    workspace.retryOverlays();
    for (const area of workspace.areas) workspace.retryTracks(area.id);
  };

  const preferences = useUiPreferences();
  const manualRailCollapsed = preferences.railCollapsed();
  const drawerWidth = useDrawerWidthHost(preferences);

  const receipts = useReadReceipts();
  const readTrackUnread = receipts.isTrackUnread;
  const isUnread = useCallback(
    (track: Track) => readTrackUnread(track.id, track.notifiableSessions),
    [readTrackUnread],
  );
  const trackGroups = useMemo(
    () => sidebarTrackGroups(workspace.tracks, workspace.areas, isUnread),
    [workspace.tracks, workspace.areas, isUnread],
  );

  const routeTrackId = routeParamFromPath(currentPath, '/track/');
  const routeAreaId = routeParamFromPath(currentPath, '/area/');
  const newTrackArea = useNewTrackDraftArea(newTrackBack === null ? undefined : routeAreaId);
  const areas = visibleAreas(workspace.areas);
  const activeAreaId = routeAreaId ?? workspace.tracks.find((track) => track.id === routeTrackId)?.areaId;
  const liveActiveArea = areas.find((area) => area.id === activeAreaId);
  const activeArea = liveActiveArea ?? newTrackArea ?? areas[0];
  const areaEditorOpen = areaEditorTarget !== null;

  // Both track mutations need the area id to invalidate the right list, and the
  // rail only knows track ids; the workspace read already has the mapping.
  const areaIdOf = (trackId: string): string | undefined =>
    workspace.tracks.find((track) => track.id === trackId)?.areaId;

  const openNewTrack = (areaId: string) => {
    go({ name: 'new-track', areaId });
  };

  /* Leaving the report layer drops `?panel=` through `closePanel()`, not a bare
   * `replace`: opening a panel is a `push`, and `replace` does not merge with the
   * entry before it. Only track routes have a report panel to close. */
  const clearReportPanel = () => {
    if (routeTrackId !== undefined) closePanel(routeTrackId);
  };

  const requestCreateArea = (origin: DialogOrigin) => {
    setAreaEditorError(areaCreateRequest === null ? null : AREA_CREATE_TEXT.unknown);
    setAreaEditorTarget({ kind: 'create', origin });
  };

  const requestEditArea = (area: Area, origin: DialogOrigin) => {
    setAreaEditorError(null);
    setAreaEditorTarget({ kind: 'edit', area, origin });
  };

  const closeAreaEditor = () => {
    if (areaEditorPending) return;
    setAreaEditorTarget(null);
    setAreaEditorError(null);
  };

  const cancelAreaEditor = () => {
    if (areaEditorPending) return;
    if (areaEditorTarget?.kind === 'create' && areaCreateRequest !== null) {
      areaIntent.release(areaCreateRequest);
    }
    closeAreaEditor();
  };

  const submitAreaEditor = (values: AreaEditorValues) => {
    const target = areaEditorTarget;
    if (target === null || areaEditorPending) return;
    const originEntryKey = (router.history as RouterHistory).location.state.__TSR_key;
    const nextTargetId = values.executionTargetId ?? 'local';
    const currentTargetId = target.kind === 'edit' ? (target.area.executionTargetId ?? 'local') : 'local';
    const patch: AreaEditorPatch | null = target.kind === 'create' ? null : {
      ...(values.name === target.area.name ? {} : { name: values.name }),
      ...(values.defaultCwd === target.area.defaultCwd ? {} : { defaultCwd: values.defaultCwd }),
      ...(nextTargetId === currentTargetId ? {} : { executionTargetId: nextTargetId }),
    };
    if (patch !== null && Object.keys(patch).length === 0) {
      setAreaEditorTarget(null);
      return;
    }
    setAreaEditorPending(true);
    setAreaEditorError(null);
    let write: Promise<Area>;
    let creation: KeyedRequest<null, NewAreaBody> | null = null;
    if (target.kind === 'create') {
      creation = areaIntent.request(null, () => ({
        name: values.name,
        color: randomAreaColor(),
        default_cwd: values.defaultCwd,
        ...(values.executionTargetId && values.executionTargetId !== 'local'
          ? { execution_target_id: values.executionTargetId }
          : {}),
      }));
      write = areaMutations.create(creation.body, creation.key);
    } else {
      write = areaMutations.update(target.area.id, {
        ...(patch?.name === undefined ? {} : { name: patch.name }),
        ...(patch?.defaultCwd === undefined ? {} : { default_cwd: patch.defaultCwd }),
        ...(patch?.executionTargetId === undefined ? {} : { execution_target_id: patch.executionTargetId }),
      });
    }
    void write.then((saved) => {
      if (creation !== null) areaIntent.release(creation);
      setAreaEditorTarget(null);
      // A successful write must not take navigation back from a newer history entry.
      if (target.kind === 'create' && originEntryKey !== undefined
        && (router.history as RouterHistory).location.state.__TSR_key === originEntryKey) {
        prepareNewTrack(saved);
        onAreaCreated?.(saved);
        openNewTrack(saved.id);
      }
    }).catch((failure: unknown) => {
      if (target.kind === 'edit') {
        setAreaEditorError(writeFailureText(AREA_PATCH_FAILURES, AREA_PATCH_TEXT)(failure));
        return;
      }
      const kind = classifyFailure(failure instanceof ApiError ? failure.failure : null, AREA_CREATE_FAILURES);
      // A refusal that never left the browser, or an answer `AREA_CREATE_FAILURES` reads as one.
      const rejected = failure instanceof AreaCreatePreflightError || failure instanceof NotSentError
        || kind === 'rejected';
      // A refused retry says nothing about an earlier unconfirmed POST; a spent key ends the request.
      const unconfirmed = target.kind === 'create' && kind !== 'key-spent'
        && (areaCreateRequest !== null || !rejected);
      if (creation !== null && !unconfirmed) areaIntent.release(creation);
      // This attempt's own words only when it was answered or stopped before sending; an unknown answer is the fixed state.
      const said = failure instanceof AreaCreatePreflightError ? failure.message
        : rejected || kind === 'key-spent' ? refusedText(writeFailureOf(failure), AREA_CREATE_TEXT.refused) : null;
      setAreaEditorError(said === null ? AREA_CREATE_TEXT.unknown : unconfirmed ? `${said} ${AREA_CREATE_TEXT.unknown}` : said);
    }).finally(() => { setAreaEditorPending(false); });
  };

  const deleteArea = (areaId: string, signal: AbortSignal) => areaMutations.remove(areaId, signal);

  const setTrackPinned = async (trackId: string, pinned: boolean) => {
    const areaId = areaIdOf(trackId);
    if (areaId === undefined) return;
    await trackMutations.setPinned(trackId, areaId, pinned, nowMs ?? Date.now());
  };

  const deleteTrack = async (trackId: string, signal: AbortSignal) => {
    const areaId = areaIdOf(trackId);
    if (areaId === undefined) return;
    await trackMutations.remove(trackId, areaId, signal);
  };

  /* The rail's receipt, key for key: opening a track on the phone clears the
     rail's dot too, and vice versa. */
  const mobileTrackActions = (track: Track) => ({
    areaPinned: preferences.areaTrackPinned(track.areaId, track.id),
    onSetPinned: (id: string, next: boolean) => {
      void pinFeedback.run(
        trackMutations.setPinned(id, track.areaId, next, nowMs ?? Date.now()),
        writeFailureText(TRACK_PATCH_FAILURES, TRACK_PATCH_TEXT.pin),
      );
    },
    onSetAreaPinned: (id: string, next: boolean) => preferences.setAreaTrackPinned(track.areaId, id, next),
    onMarkUnread: (id: string) => {
      void pinFeedback.run(
        receipts.markUnread('track', id),
        writeFailureText(READ_RECEIPT_FAILURES, MARK_UNREAD_TEXT),
      );
    },
  });

  return {
    workspace,
    areas,
    readError,
    activityError,
    readLoading,
    retryRead,
    executionTargets: executionTargetsQuery.data,
    preferences,
    manualRailCollapsed,
    drawerWidth,
    currentPath,
    routeOwnsMobileChrome,
    settingsOpen,
    mobileOverlayRoute,
    routeTrackId,
    routeAreaId,
    newTrackArea,
    newTrackBack,
    liveActiveArea,
    activeArea,
    isUnread,
    trackGroups,
    pinFeedback,
    go,
    areaIdOf,
    clearReportPanel,
    openNewTrack,
    deleteArea,
    setTrackPinned,
    deleteTrack,
    mobileTrackActions,
    requestCreateArea,
    requestEditArea,
    areaEditorOpen,
    areaEditor: {
      target: areaEditorTarget,
      pending: areaEditorPending,
      error: areaEditorError,
      createRequest: areaCreateRequest,
      nameInputRef: areaEditorNameRef,
      executionTargets: executionTargetsQuery.data,
      listDirectory,
      listDirectoryForTarget,
      onCreateExecutionTarget: createExecutionTarget,
      onClose: closeAreaEditor,
      onCancel: cancelAreaEditor,
      onSubmit: submitAreaEditor,
    },
  };
}

export type ShellController = ReturnType<typeof useShellController>;
