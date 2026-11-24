import { permissionModesForProvider } from '../../../../core/domain/agent-providers.ts';
import { providerPresentation } from '../../../../core/domain/provider-presentation.ts';
import { conversationCommandEnabled } from '../../../../core/domain/conversation-action.ts';
import { permissionModeAfterProviderChange,resolvePermissionControl } from '../../../../core/domain/planner-permission-mode.ts';
import { composerCommands,conversationCommandBindings,conversationCommands } from '../../features/chat/thread/composer-command.ts';
import { PendingQueue } from '../../features/chat/thread/pending-queue.tsx';
import { useThreadReadingView } from '../../features/chat/thread/reading-view.tsx';
import { ConversationSessionHeader } from '../../features/chat/thread/session-header.tsx';
import { useContentAdmission } from '../../ui/content-presentation/public.ts';
import type { PaneResizeGroup } from '../../ui/drawer/resize-group.ts';
import { writeClipboardText } from '../../ui/operation-feedback/clipboard.ts';
import { ConversationApprovalControl } from '../conversations/approval-control.tsx';
import type { ConversationRouteIntent,PlannerConversationScope } from '../conversations/contracts.ts';
import { createConversationDraftActions } from '../conversations/draft-actions.ts';
import { useConversationDraftAutoSend,useConversationDraftRetention,useConversationEscape,useConversationPaneTarget,useRequestedConversationOpen } from '../conversations/pane-lifecycle.ts';
import { useConversationStore } from '../conversations/store.ts';
import { managedProvidersQueryOptions,useManagedProviders } from '../providers/managed-providers.ts';
import { useConversationActionArea } from './conversation-action-area.tsx';
import { useConversationInitialSend } from './conversation-initial-send.ts';
import { useConversationLinks } from './conversation-links.tsx';
import { usePlannerSwitchControl } from './planner-switch-control.ts';

import { HStack } from '@astryxdesign/core/HStack';
import { useQuery } from '@tanstack/react-query';
import { createRef,useCallback,useEffect,useMemo,useRef,type Dispatch,type SetStateAction } from 'react';

import type { AgentProvider, AskAnswer } from '../../../../core/domain/presentation-types.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { approvalsForSession,type PendingApproval } from '../../../../core/domain/approval.ts';
import { type OpenAsk } from '../../../../core/domain/ask.ts';
import {
conversationName,
isUserConversationTurn,
type Conversation,
type ConversationTurn,
type PendingQueueEntry,
type TranscriptEntry
} from '../../../../core/domain/conversation.ts';
import {
type ReportFileLinkTarget
} from '../../../../core/domain/report-file.ts';
import {
type ReportLinkTarget
} from '../../../../core/domain/report.ts';
import {
toTrack
} from '../../../../core/domain/track.ts';
import { ChatList } from '../../features/chat/list/public.tsx';
import { ContextRing } from '../../features/chat/thread/context-ring.tsx';
import { useMentionTrigger } from '../../features/chat/thread/mention-trigger.tsx';
import { ModelPill,PermissionModePill } from '../../features/chat/thread/model-pill.tsx';
import { MessageImages } from '../../features/chat/thread/message-images.tsx';
import { OutgoingMessage } from '../../features/chat/thread/outgoing-message.tsx';
import {
ChatEmpty,
ChatFooterError,ChatFooterNotice,ChatFooterRemedy,ChatThread,
type ComposerFocusHandle,
} from '../../features/chat/thread/public.tsx';
import {
PlannerAttachButton,usePlannerAttachments,
type AttachmentDraft,
} from '../../features/planner/attachments.tsx';
import { ComposerAttachmentStrip } from '../../ui/composer-attachments/public.tsx';
import { useCommittedCallback } from '../../ui/state/committed-callback.ts';
import { useState } from '../../ui/state/public.ts';
import { useCompactViewport } from '../../ui/viewport/public.ts';
import {
useConversationAttachments,
useConversationCommands,
useConversationDraft,
useConversationAttachmentError,
type ConversationDraftId
} from '../conversations/public.tsx';
import { createTrackWorkspaceFilesPort } from '../providers/directory.ts';
import { useMentionSearch } from '../providers/mentions.ts';
import { useReadReceipt,useReadReceipts,useViewReadReceipt } from '../providers/read-receipts.tsx';
import { trackDetailQueryOptions } from '../providers/track-queries.ts';
import { useConversationDrawerResize } from '../shell/public.tsx';
import footerStyles from './composer-footer.module.css';
import { ConversationComposer,ConversationDock } from './conversation-composer.tsx';
import { ConversationSurface,type EmbeddedConversationPresentation } from './mobile-conversation-surface.tsx';
import {
useGo,
useTrackFileNavigation
} from './navigation.ts';

import type { ConversationPanelOptions,ConversationPanelSource } from './conversation-panel-contract.ts';
const NO_ASKS: readonly OpenAsk[] = Object.freeze([]);
export function useConversationPane(
  transport: ApiTransportPort,
  unauthorized: UnauthorizedChannel,
  source: ConversationPanelSource,
  target: ReturnType<typeof useConversationPaneTarget>,
  options?: { onPresent?: () => void; embedded?: EmbeddedConversationPresentation; onArchive?: (cardId: string, archived: boolean) => Promise<void>; onTerminal?: () => void; retainCompanion?: boolean; companionVisible?: boolean; reportComposer?: Readonly<{ ready: boolean; returnLabel: string; conversationId: string | null }>; showSideCommand?: boolean; stacked?: boolean; ownedCardIds?: readonly string[]; showTrack?: boolean; resizable?: boolean; inline?: boolean; slotId?: string; enabled?: boolean; presented?: boolean;
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
    ?? (imageTrack.data === undefined ? null : toTrack(imageTrack.data.track).agentCwd);
  const conversationFiles = useMemo(() => imageTrackId === null ? null
    : createTrackWorkspaceFilesPort(transport, unauthorized, imageTrackId), [imageTrackId, transport, unauthorized]);
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
    plannerRow === null ? null : plannerRow.areaId, source.scopeId, undefined));
  /* The open conversation's own composer: words and images live in the registry per conversation,
       so closing keeps them and switching shows the other conversation's own. */
  const composerId = scope?.cardId ?? null;
  const heldAttachments = useConversationAttachments(composerId);
  const attachmentError = useConversationAttachmentError(composerId);
  const scopeTrackId = scope?.id;
  useEffect(() => {
    registry.bindDraftScope(sourceScopeId, source.scopeId);
    if (composerId !== null && scopeTrackId !== undefined) registry.bindConversation(composerId, scopeTrackId);
  }, [registry, sourceScopeId, source.scopeId, composerId, scopeTrackId]);
  const { editComposer } = registry;
  const setComposerText = useCallback<Dispatch<SetStateAction<string>>>((action) => {
    if (composerId === null) return;
    editComposer(composerId, (current) => {
      const text = typeof action === 'function' ? action(current.text) : action;
      return text === current.text ? current : { ...current, text };
    });
  }, [composerId, editComposer]);
  const changeAttachments = useCallback((change: (current: AttachmentDraft) => Required<AttachmentDraft>) => {
    if (composerId === null) return;
    editComposer(composerId, current => ({ ...current, ...change(current) }));
  }, [composerId, editComposer]);
  const attachments = usePlannerAttachments({ attachments: heldAttachments, attachmentError }, changeAttachments);
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
    isUserConversationTurn(entry) && (entry.text !== '' || (entry.attachments?.length ?? 0) > 0));
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
  const changeDraftAttachments = (change: (current: AttachmentDraft) => Required<AttachmentDraft>) => {
    if (draft === null) return;
    registry.editDraft(draft, current => current.creating || current.submitted !== null
      ? current : { ...current, ...change(current) });
  };
  const draftAttachments = usePlannerAttachments(draft ?? {}, changeDraftAttachments);
  const draftImagesSupported = draftCapabilities.data?.conversationInitialInput === true;
  const draftImagesLocked = creating || draft?.submitted != null;
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
    registry, draft, creating, source, sourceScopeId, supportsDraftModel,
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
  useConversationInitialSend({ primaryPlanner: options?.slotId === undefined && source.planner != null && open?.id === source.planner.cardId,
    scopeId: source.scopeId, plannerCardId: source.planner?.cardId, open, store, registry, focusComposer });

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
    () => approvalsForSession(source.pendingApprovals, openRowId, store.pendingApprovals),
    [ source.pendingApprovals, openRowId, store.pendingApprovals],
  );
  const conversationAsks: readonly OpenAsk[] = store.pendingInteractions?.flatMap(interaction => interaction.kind === 'ask' && interaction.canRespond
    ? [{ askId: interaction.id, delivery: 'wake' as const, questions: interaction.questions?.map(question => ({ title: question.question, options: question.options })) ?? [] }] : []) ?? NO_ASKS;
  /* Read field by field: the control is rebuilt each render, and the composer's memo keys on what it shows. */
  const decideApproval = useCommittedCallback(existingId, (approval: PendingApproval, optionId: string) =>
    source.decideApproval(approval, optionId));
  const answerAsk = useCommittedCallback(existingId, (askId: import('../../../../core/domain/activity.ts').AskId, answers: readonly AskAnswer[]) =>
    store.answerAsk(askId, answers));
  const attach = useCommittedCallback(existingId, attachments.attach);
  const removeAttachment = useCommittedCallback(existingId, attachments.remove);
  const { items: attachmentItems, error: imageError, atCapacity } = attachments;
  const composerAttachments = useMemo(() => ({ items: attachmentItems, error: imageError, atCapacity, attach, remove: removeAttachment }),
    [attachmentItems, imageError, atCapacity, attach, removeAttachment]);
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
    attachmentsSupported: store.attachmentsSupported,
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
  }), [compact, store.compacting, store.manualCompactSupported, store.attachmentsSupported, store.contextUsage, store.historyReady, store.runReady, interrupt, store.model, store.modelCatalog, store.modelDefaultAvailable, store.modelChangeDisabled, store.permissionChangeDisabled,
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
  const sessionConfigurationControls = useMemo(() => <>
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
  </>, [existingId, scopeProvider, composerView, permissionControl]);

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
              /* Admission transfers these previews to the conversation outbox. */
              commands={paneCommands}
              onSend={sendText}
              canQueueWhileRunning={composerView.canQueueWhileRunning}
              allowEmptyText={!editingQueue && composerAttachments.items.length > 0}
              onFiles={editingQueue || !composerView.attachmentsSupported || composerView.sendBlocked || draftOnly
                ? undefined : composerAttachments.attach}
              /* Questions and attachments belong to the composer; waiting messages follow the running status. */
              drawer={<ComposerAttachmentStrip items={composerAttachments.items} error={composerAttachments.error} onRemove={composerAttachments.remove} />}
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
                    available={composerView.attachmentsSupported}
                    disabled={editingQueue || composerView.sendBlocked || !composerView.historyReady || replacing}
                  />
                  {sessionConfigurationControls}
                </HStack>
              )}
            />
  ), [existingId, compactComposer, panePresented, composerFocusFor, composerFocusRequest, changeComposer, composerView, draftOnly,
    paneCommands, composerStop, replacing, editingQueue,
    sendText, composerAttachments, mentionTrigger, sessionConfigurationControls]);

  const { actionArea, changeArchive } = useConversationActionArea({ open, options, store, shownComposer, existingId,
    conversationAsks, conversationApprovals, decideApproval, answerAsk, retrySend, focusComposer });

  const centeredEmpty = options?.embedded?.active === true && open !== null && store.historyReady && openTurns.length === 0 && !hasPendingMessages && !(pendingPresentation?.conversationId === existingId && pendingPresentation.visible) && !store.sending && !store.working && store.failedSend === null;

  const renderDrawer = (resizeGroup: PaneResizeGroup | null = null) => (
      <ConversationSurface
        embedded={options?.embedded}
        centerEmptyComposer={centeredEmpty}
        mobileSheet={compactComposer}
        focusInput={panePresented && (draftOpen || (open !== null && composerFocusFor === open.id))}
        contextTitle={scope?.title}
        sessionHeader={open === null ? undefined : <ConversationSessionHeader session={store.session} state={store.sessionState}
          taskId={store.taskId} readonlyReason={store.readonlyReason} workerPaused={store.workerPaused}
          compactEnabled={commandEnabled('compact') && store.manualCompactSupported}
          resetEnabled={commandEnabled('reset')}
          onCompact={compact} onReset={() => store.recover('reset_context')}
          onArchive={store.session?.isLead === true || store.taskId != null || options?.onArchive === undefined || (store.readonlyReason !== null && !store.canReopen) ? undefined : () => changeArchive(store.readonlyReason !== 'archived')} />}
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
        title={options?.inline === true ? `Side conversation · ${providerPresentation(open?.provider ?? draft?.provider ?? scopeProvider).label}`
          : open !== null ? conversationName(open) : draftOpen ? 'Untitled' : ''}
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
              allowEmptyText={draftAttachments.items.length > 0}
              onFiles={draftImagesSupported && !draftImagesLocked ? draftAttachments.attach : undefined}
              drawer={<ComposerAttachmentStrip items={draftAttachments.items} error={draftAttachments.error} onRemove={draftAttachments.remove} disabled={draftImagesLocked} />}
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
                      attachments={draftAttachments}
                      available={draftImagesSupported}
                      disabled={draftImagesLocked}
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

                  </HStack>
                );
              })()} />
          </>
        ) : open === null ? undefined : (
          <>
            {actionArea}
            {store.readonlyReason === null && composerNode}
            {store.readonlyReason === 'waiting_task' && <HStack gap={1} align="center" wrap="wrap" className={footerStyles.group}>
              {sessionConfigurationControls}
            </HStack>}
          </>
        )}
      >

        {(draft?.side !== undefined && draftOpen || open?.sourceCardId !== undefined) && (
          <p>From {contextSource === undefined ? 'an earlier conversation' : conversationName(contextSource)}.
            {' '}Uses a snapshot of loaded text, not complete history. Choose the backend before sending.</p>
        )}
        {/* Shown back because the composer clears its field on send; nothing here
                    claims they arrived. */}
        {draftOpen && (draft?.text == null
          ? <ChatEmpty />
          : (draft.text !== '' && <OutgoingMessage speaker="you" text={draft.text} mobile={compactViewport} />))}
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
            {!threadPresented && outgoingPreview !== undefined && <>
              {outgoingPreview.text !== '' && <OutgoingMessage speaker={outgoingPreview.author} text={outgoingPreview.text} mobile={compactViewport} />}
              {outgoingPreview.attachments?.length ? <MessageImages attachments={outgoingPreview.attachments} /> : null}
            </>}
            {!threadPresented && hasPendingMessages && pendingMessages}
            {threadPresented && !centeredEmpty && (
              <ChatThread historyPrefetch={historyPrefetch} readingView={threadReadingView} key={open.id} pendingMessages={pendingMessages}
                hasPendingMessages={hasPendingMessages || (pendingPresentation?.conversationId === existingId && pendingPresentation.visible)}
                conversation={open}
                imageFiles={imageFiles}
                renderLink={chatLinks.renderLink}
                turns={openTurns} lastSessionTurn={store.lastTurn} sessionTurns={store.sessionTurns}
                runtimeStatus={store.runtimeStatus}
                runtimeTasks={store.runtimeTasks}
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
