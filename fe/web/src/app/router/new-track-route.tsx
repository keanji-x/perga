import { queryKeys } from '../providers/query-keys.ts';
import { Banner } from '@astryxdesign/core/Banner';
import { useQuery } from '@tanstack/react-query';
import { useCallback,useEffect,useRef } from 'react';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { visibleAreas, type Area } from '../../../../core/domain/area.ts';
import { availabilityOf,permissionModesForProvider } from '../../../../core/domain/agent-providers.ts';
import { ApiError,classifyFailure,NotSentError,refusedText,writeFailureOf } from '../../../../core/domain/failure-class.ts';
import { resolvePermissionControl } from '../../../../core/domain/planner-permission-mode.ts';
import { readErrorText } from '../../../../core/domain/read-failure.ts';
import {
isBlankForKernel,TRACK_CREATE_FAILURES,TRACK_CREATE_TEXT,trackCreateWorkspaceError,
trackWorkspaceOptionsOperation,type NewTrackBody,
} from '../../../../core/domain/track.ts';
import { NewTrackForm,type NewTrackDraft,type NewTrackFormState } from '../../features/area/new-track/public.tsx';
import { useMentionTrigger } from '../../features/chat/thread/mention-trigger.tsx';
import { ModelPill,PermissionModePill } from '../../features/chat/thread/model-pill.tsx';
import { PlannerAttachButton, usePlannerAttachments, type AttachmentDraft } from '../../features/planner/attachments.tsx';
import { ComposerAttachmentStrip } from '../../ui/composer-attachments/public.tsx';
import { ErrorBox } from '../../ui/error-box/public.tsx';
import { useCompactViewport } from '../../ui/viewport/public.ts';
import { useConversationCommands } from '../conversations/public.tsx';
import { agentProvidersQueryOptions } from '../providers/agent-providers.ts';
import { appSettingsQueryOptions } from '../providers/app-settings.ts';
import { createDirectoryLister } from '../providers/directory.ts';
import { mintIdempotencyKey } from '../providers/idempotency-key.ts';
import { useManagedProviderCatalogs } from '../providers/managed-providers.ts';
import { useMentionSearch } from '../providers/mentions.ts';
import { executionTargetsQueryOptions, useCreateExecutionTarget } from '../providers/execution-target-queries.ts';
import { runOperation } from '../providers/query-operation.ts';
import { useTrackLifecycleMutations } from '../providers/track-lifecycle-mutations.ts';
import { useWorkspace, type Workspace } from '../providers/workspace-queries.ts';
import { useGo,useRouteParam } from './navigation.ts';
import { useNewTrackSession,type NewTrackSession,type TrackCreationRequest } from './new-track-drafts.tsx';

type RouteProps = { transport: ApiTransportPort; unauthorized: UnauthorizedChannel; memoryEnabled?: boolean };

export function NewTrackRoute({ transport, unauthorized, memoryEnabled = true }: RouteProps) {
  const areaId = useRouteParam('/area/');
  const workspace = useWorkspace(transport, unauthorized);
  const area = workspace.areas.find((candidate) => candidate.id === areaId);
  const { store, session } = useNewTrackSession(areaId ?? '', area);
  const go = useGo();
  if (session === null) {
    if (workspace.areasLoading) return null;
    if (workspace.areasError !== null) return <ErrorBox message={readErrorText(workspace.areasError, 'Areas are unavailable.')} onRetry={workspace.retryAreas} />;
    if (area !== undefined) return null; // The provider initializes the Area draft after this read.
    return <ErrorBox message="This area could not be found." onRetry={() => go({ name: 'today' })} />;
  }
  return <NewTrackEditor key={session.area.id} transport={transport} unauthorized={unauthorized} memoryEnabled={memoryEnabled}
    workspace={workspace} area={area} session={session} store={store} />;
}

function NewTrackEditor({ transport, unauthorized, memoryEnabled, workspace, area, session, store }: RouteProps & {
  workspace: Workspace;
  area: Area | undefined;
  session: NewTrackSession;
  store: ReturnType<typeof useNewTrackSession>['store'];
}) {
  const compactViewport = useCompactViewport();
  const areaId = session.area.id;
  const targetId = session.form?.executionTargetId ?? session.area.executionTargetId ?? 'local';
  const executionTargetsQuery = useQuery({
    ...executionTargetsQueryOptions(transport, unauthorized),
    retry: false,
  });
  const createExecutionTarget = useCreateExecutionTarget(transport, unauthorized);
  const listDirectoryForTarget = useCallback(
    (nextTargetId: string) => createDirectoryLister(transport, unauthorized, nextTargetId),
    [transport, unauthorized],
  );
  /* Availability has one cache owner; the managed response supplies only static creation capability.
     The server admits each submitted shape and renders any refusal through the existing error flow. */
  const availability = useQuery(agentProvidersQueryOptions(transport, unauthorized, targetId));
  const settings = useQuery(appSettingsQueryOptions(transport, unauthorized));
  const { providers, catalogs } = useManagedProviderCatalogs(transport, unauthorized, targetId);
  const optionCwd = session.form?.cwd ?? session.area.defaultCwd ?? '';
  const workspaceOptionsQuery = useQuery({
    queryKey: queryKeys.trackWorkspaceOptions(areaId, targetId, optionCwd),
    queryFn: ({ signal }: { signal: AbortSignal }) =>
      runOperation(transport, { ...trackWorkspaceOptionsOperation(areaId, optionCwd, undefined, targetId), signal }, unauthorized),
    enabled: session.form !== null && session.request === null,
    retry: false,
  });
  const trackMutations = useTrackLifecycleMutations(transport, unauthorized);
  const conversationRegistry = useConversationCommands();
  const go = useGo();
  const listDirectory = listDirectoryForTarget(targetId);
  /* The sentence is the new track's Planner's first message, and that Planner reads this Area's reports; there is no track yet to rank first. */
  const mentionTrigger = useMentionTrigger(useMentionSearch(transport, unauthorized, areaId, null, undefined,
    memoryEnabled ? areaId : false), memoryEnabled);

  const savedProvider = settings.isSuccess ? settings.data.values['planners.default_provider'] : undefined;
  const defaultProvider = typeof savedProvider === 'string' && availabilityOf(availability.data, savedProvider)?.status === 'ready'
    ? savedProvider : availability.data?.find((entry) => entry.status === 'ready')?.provider ?? 'codex';
  const provider = session.request?.body.provider ?? session.provider ?? defaultProvider;
  const available = workspace.areasError === null && !workspace.areasLoading
    && area !== undefined;
  const liveRef = useRef(true);
  useEffect(() => {
    liveRef.current = true;
    return () => { liveRef.current = false; };
  }, []);
  const saveForm = useCallback((form: NewTrackFormState) => { store.update(areaId, { form }); }, [areaId, store]);
  const changeAttachments = useCallback((change: (current: AttachmentDraft) => Required<AttachmentDraft>) => {
    const current = store.get(areaId);
    if (current === null || current.creating || current.request !== null || current.createdTrackId !== null) return;
    store.update(areaId, change(current));
  }, [areaId, store]);
  const attachments = usePlannerAttachments(
    { attachments: session.attachments, attachmentError: session.attachmentError },
    changeAttachments,
  );
  const configurationLocked = session.creating || session.request !== null || !available || session.createdTrackId !== null;

  const openCreated = (trackId: string) => {
    const synced = conversationRegistry.hydratePendingTrackInitialSends();
    const request = store.get(areaId)?.request;
    const hasAttachments = (request?.attachments?.length ?? 0) > 0;
    const hasMessage = (request?.message !== undefined && !isBlankForKernel(request.message)) || hasAttachments;
    const canForget = !hasMessage || synced;
    if (!canForget && !conversationRegistry.hasPendingTrackInitialSend(trackId)) {
      store.update(areaId, { error: 'Your track was created, but its saved first message is unavailable. Try opening the track again.' });
      return;
    }
    // Keep the saved request as a recovery copy while the first message is only in memory.
    if (canForget) store.forget(areaId, { revokeAttachments: false });
    go({ name: 'track', trackId, openPlanner: true });
  };

  const submit = (draft: NewTrackDraft) => {
    // The provider owns the lease too: leaving and returning during a POST
    // must not enable a second request before the first settles.
    const current = store.get(areaId);
    // After `key-reused` only an explicit new track goes anywhere: a retry under the key cannot.
    if (current === null || current.creating || !available || current.createdTrackId !== null
      || current.canRetryAsNewTrack) return;
    const hadUnconfirmedRequest = current.request !== null;
    const attemptKey = current.key;
    const body = {
      areaId: areaId,
      provider: current.provider ?? provider,
      ...(current.permissionMode === null ? {} : { permissionMode: current.permissionMode }),
      ...(current.model.model === null ? {} : { model: current.model.model }),
      ...(current.model.reasoning_effort === null ? {} : { reasoningEffort: current.model.reasoning_effort }),
      ...(draft.cwd === undefined ? {} : { cwd: draft.cwd }),
      ...(draft.workspaceOptionId === undefined ? {} : { cwd: draft.cwd ?? '', workspaceOptionId: draft.workspaceOptionId }),
      ...(draft.executionTargetId === undefined ? {} : { executionTargetId: draft.executionTargetId }),
    } satisfies NewTrackBody;
    const draftAttachments = current.attachments ?? [];
    // The original wire body travels with the key. A retry after navigation,
    // theme changes or template refresh must not silently change its payload.
    const request: TrackCreationRequest = current.request !== null
      ? current.request
      : {
          body,
          key: attemptKey,
          ...(isBlankForKernel(draft.message) ? {} : { message: draft.message }),
          ...(draftAttachments.length > 0 ? { attachments: draftAttachments } : {}),
        };
    store.update(areaId, { creating: true, request, error: null, canRetryAsNewTrack: false });
    const creation = trackMutations.create(request.body, request.key);
    void creation.then((track) => {
      const requestAttachments = request.attachments ?? [];
      const hasInitialMessage = (request.message !== undefined && !isBlankForKernel(request.message))
        || requestAttachments.length > 0;
      const persistConfirmation = !hasInitialMessage
        || conversationRegistry.queueTrackInitialSend(
          track.id,
          request.message ?? '',
          `${request.key}:init`,
          requestAttachments,
        );
      // A late acknowledgement belongs to its draft; it never steals the
      // navigation the reader made while waiting. Returning offers Open track.
      // If saving the first send failed, the saved request can still replay both keys after reload.
      store.update(areaId, { createdTrackId: track.id }, { persistConfirmation });
      if (liveRef.current) openCreated(track.id);
    }).catch((failure: unknown) => {
      const apiFailure = failure instanceof ApiError ? failure.failure : null;
      const kind = classifyFailure(apiFailure, TRACK_CREATE_FAILURES);
      const rejectedBeforeDispatch = failure instanceof NotSentError;
      store.update(areaId, {
        // Not sent leaves an earlier unconfirmed create as it was; an unknown answer is the fixed state, never its text.
        error: rejectedBeforeDispatch ? (hadUnconfirmedRequest ? TRACK_CREATE_TEXT.unknown : TRACK_CREATE_TEXT.refused)
          : kind === 'unconfirmed' ? (trackCreateWorkspaceError(apiFailure) ?? TRACK_CREATE_TEXT.unknown) : refusedText(writeFailureOf(failure), TRACK_CREATE_TEXT.refused),
        ...(kind === 'exhausted' ? { key: mintIdempotencyKey(), request: null } : {}),
        // A refused retry says nothing about the earlier attempt's outcome.
        ...(!hadUnconfirmedRequest && (rejectedBeforeDispatch || kind === 'rejected') ? { request: null } : {}),
        canRetryAsNewTrack: kind === 'key-reused',
      });
    }).finally(() => { store.update(areaId, { creating: false }); });
  };

  const retryAsNewTrack = () => {
    store.update(areaId, { key: mintIdempotencyKey(), request: null, canRetryAsNewTrack: false, error: null });
  };
  const areaFailure = workspace.areasError !== null
    ? readErrorText(workspace.areasError, 'Areas could not be refreshed. Your draft is kept here.')
    : !workspace.areasLoading && !available ? 'Area unavailable. Your draft is kept here; select and copy it to use elsewhere.' : null;
  const createdTrackId = session.createdTrackId;

  const permissionControl = resolvePermissionControl({
    storedMode: session.permissionMode,
    providerModes: permissionModesForProvider(providers.data, provider),
    metadataLoaded: providers.data !== undefined,
    isDisabled: session.creating || session.request !== null,
  });

  return <NewTrackForm
    area={area ?? session.area}
    areas={visibleAreas(workspace.areas)}
    onSelectArea={(nextAreaId) => go({ name: 'new-track', areaId: nextAreaId })}
    workspaceOptions={{ cwd: optionCwd, targetId,
      result: !workspaceOptionsQuery.isError ? workspaceOptionsQuery.data ?? null : null }}
    readNotice={workspace.areasLoading ? <Banner status="info" role="status" title="Loading Area…"
      description="You can keep writing. Create track will be available when loading finishes." />
      : workspaceOptionsQuery.isError && session.request === null
        ? <ErrorBox message={readErrorText(workspaceOptionsQuery.error, 'Workspace options could not be loaded.')}
          onRetry={() => { void workspaceOptionsQuery.refetch(); }} />
        : undefined}
    mentionTrigger={mentionTrigger}
    allowEmptyText={attachments.items.length > 0}
    onFiles={configurationLocked ? undefined : attachments.attach}
    drawer={<ComposerAttachmentStrip items={attachments.items} error={attachments.error} onRemove={attachments.remove} disabled={configurationLocked} />}
    attachButton={<PlannerAttachButton attachments={attachments} available disabled={configurationLocked} />}
    modelControls={<>
      {permissionControl.visible && (
        <PermissionModePill
          mode={permissionControl.mode}
          options={permissionControl.options}
          onChange={(permissionMode) => { store.update(areaId, { provider, permissionMode }); }}
          isDisabled={permissionControl.isDisabled}
        />
      )}
      <ModelPill
        groups={(providers.data ?? []).map((entry, index) => ({
          provider: entry.availability.provider,
          catalog: catalogs[index].data ?? null,
          availability: availabilityOf(availability.data, entry.availability.provider),
          acceptsUnavailableBlankTrack: entry.accepts_unavailable_blank_track,
        }))}
        provider={provider}
        effortControl={compactViewport ? 'in-menu' : 'separate'}
        selection={session.model}
        /* The pick is the Planner's provider too (#1810); a pick never carries another provider's effort. */
        onChange={(model, provider) => { store.update(areaId, { provider, model }); }}
        isDisabled={configurationLocked}
      />
    </>}
    initialDraft={session.form ?? undefined}
    onDraftChange={saveForm}
    submitBlocked={!available || createdTrackId !== null || session.canRetryAsNewTrack}
    locked={session.request !== null}
    submitting={session.creating}
    error={areaFailure ?? session.error ?? (createdTrackId !== null ? 'Your track was created while you were away.' : null)}
    errorAction={createdTrackId !== null && session.request !== null
      ? { label: 'Open track', onClick: () => { openCreated(createdTrackId); } }
      : workspace.areasError !== null
        ? { label: 'Retry', onClick: workspace.retryAreas }
        : session.canRetryAsNewTrack ? { label: 'Start as a new track', onClick: retryAsNewTrack } : undefined}
    initialCwd={session.area.defaultCwd}
    initialExecutionTargetId={session.area.executionTargetId}
    executionTargets={executionTargetsQuery.data}
    listDirectory={listDirectory}
    listDirectoryForTarget={listDirectoryForTarget}
    onCreateExecutionTarget={createExecutionTarget}
    onSubmit={submit}
  />;
}
